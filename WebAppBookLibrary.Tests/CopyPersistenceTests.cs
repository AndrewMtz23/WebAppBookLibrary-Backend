using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Tests;

public sealed class CopyPersistenceTests
{
    [LocalMongoFact]
    public Task Account_deletion_anonymizes_copy_history_and_idempotency_identifiers()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await Seed(db);var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("PRIV-001","good",null,null,"create"),f.Actor,default)).Copy!;
        Assert.True((await new CopyTransitions(f.Db,TimeProvider.System).ChangeStatusAsync(copy.Id,new("maintenance","Repair",1,"change"),f.Actor,default)).Success);
        var accounts=new MongoAdminUserStore(f.Db);
        await accounts.SetStatusSafelyAsync(admin.Id,f.Actor.UserId,false,DateTime.UtcNow,default);
        Assert.Equal(AdminStoreMutationOutcome.Success,(await accounts.DeletePermanentlyAsync(admin.Id,f.Actor.UserId,default)).Outcome);
        var history=await db.GetCollection<CopyHistoryEntry>("CopyHistory").Find(h=>h.CopyId==copy.Id).ToListAsync();
        Assert.Equal(2,history.Count);Assert.All(history,h=>{Assert.Null(h.ActorId);Assert.Null(h.OperationKey);Assert.Null(h.OperationFingerprint);});
        Assert.Null((await db.GetCollection<BookCopy>("BookCopies").Find(c=>c.Id==copy.Id).SingleAsync()).CreationKey);
    });
    [LocalMongoFact]
    public Task Creation_does_not_silently_repair_discrepant_projections()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await Seed(db);
        await f.Db.Books.UpdateOneAsync(b=>b.Id==f.Book.Id,Builders<Book>.Update.Set(b=>b.AvailableCopies,7));
        var result=await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("BAD-001","good",null,null,"bad"),f.Actor,default);
        Assert.Equal("inventory_reconciliation_required",result.ErrorCode);
        Assert.Equal(7,(await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync()).AvailableCopies);
        Assert.Equal(0,await db.GetCollection<BookCopy>("BookCopies").CountDocumentsAsync(FilterDefinition<BookCopy>.Empty));
    });
    internal static async Task<(MongoDBService Db,Book Book,CopyActor Actor)> Seed(IMongoDatabase db)
    {
        var f=await ReadingPersistenceTests.Seed(db);
        var staff=await SupportPersistenceTests.UserAsync(f.Db,"librarian");
        f.Book.InventoryMode="unit";f.Book.TotalCopies=0;f.Book.AvailableCopies=0;f.Book.RetainedCopies=0;
        await f.Db.Books.ReplaceOneAsync(b=>b.Id==f.Book.Id,f.Book);
        await CopyIndexes.CreateAsync(db);
        return(f.Db,f.Book,new(staff.Id,false));
    }

    [LocalMongoFact]
    public Task Concurrent_creation_and_retries_never_inflate_inventory()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await Seed(db);var store=new CopyStore(f.Db,TimeProvider.System);
        var request=new CreateCopyRequest(" cp-001 ","good","A1",null,"create-one");
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>store.CreateAsync(f.Book.Id,request,f.Actor,default)));
        Assert.All(results,r=>Assert.True(r.Success));
        Assert.Equal(results[0].Copy!.Id,results[1].Copy!.Id);
        Assert.Equal("CP-001",results[0].Copy!.InventoryCode);
        var book=await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync();
        Assert.Equal(1,book.TotalCopies);Assert.Equal(1,book.AvailableCopies);
        Assert.Equal("idempotency_conflict",(await store.CreateAsync(f.Book.Id,request with {Condition="worn"},f.Actor,default)).ErrorCode);
        Assert.False((await store.CreateAsync(f.Book.Id,request with {IdempotencyKey="different"},f.Actor,default)).Success);
        Assert.Equal(1,await db.GetCollection<BookCopy>("BookCopies").CountDocumentsAsync(FilterDefinition<BookCopy>.Empty));
    });

    [LocalMongoFact]
    public Task Damaged_units_are_unavailable_and_stale_updates_do_not_overwrite_metadata()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await Seed(db);var store=new CopyStore(f.Db,TimeProvider.System);
        var copy=(await store.CreateAsync(f.Book.Id,new("CP-002","damaged",null,null,"damaged"),f.Actor,default)).Copy!;
        Assert.Equal("maintenance",copy.Status);
        Assert.True((await store.UpdateAsync(copy.Id,new(1,"Repair","Split spine"),f.Actor,default)).Success);
        Assert.Equal("version_conflict",(await store.UpdateAsync(copy.Id,new(1,"Shelf",null),f.Actor,default)).ErrorCode);
        var query=new CopyQueries(f.Db);var detail=await query.GetAsync(copy.Id,default);
        Assert.Equal("Repair",detail!.Location);Assert.Equal(2,detail.Version);
        var page=await query.ListAsync(f.Book.Id,"available",1,20,default);
        Assert.Empty(page.Items);Assert.Equal(1,page.StateCounts!["maintenance"]);
        Assert.Equal(0,(await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync()).AvailableCopies);
    });

    [LocalMongoFact]
    public Task Unit_inventory_cannot_be_overwritten_by_old_book_forms_or_deleted()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await Seed(db);var store=new CopyStore(f.Db,TimeProvider.System);
        Assert.True((await store.CreateAsync(f.Book.Id,new("CP-003","good",null,null,"protect"),f.Actor,default)).Success);
        var current=await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync();var books=new MongoBookStore(f.Db);
        current.TotalCopies=99;
        Assert.False(await books.ReplaceMetadataAsync(current,current.UpdatedAt,default));
        current=await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync();current.MediaType="digital";
        Assert.False(await books.ReplaceMetadataAsync(current,current.UpdatedAt,default));
        await books.SetActiveAsync(current.Id,false,DateTime.UtcNow,default);
        Assert.Equal("book_has_references",(await books.DeletePermanentlyAsync(current.Id,default)).ErrorCode);
    });

    [LocalMongoFact]
    public Task Store_rechecks_role_and_refuses_aggregate_titles()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await Seed(db);var store=new CopyStore(f.Db,TimeProvider.System);var request=new CreateCopyRequest("CP-004","good",null,null,"role");
        await f.Db.Users.UpdateOneAsync(u=>u.Id==f.Actor.UserId,Builders<User>.Update.Set(u=>u.Role,"user"));
        Assert.Equal("copy_forbidden",(await store.CreateAsync(f.Book.Id,request,f.Actor,default)).ErrorCode);
        await f.Db.Users.UpdateOneAsync(u=>u.Id==f.Actor.UserId,Builders<User>.Update.Set(u=>u.Role,"librarian"));
        await f.Db.Books.UpdateOneAsync(b=>b.Id==f.Book.Id,Builders<Book>.Update.Set(b=>b.InventoryMode,"aggregate"));
        Assert.Equal("copy_conversion_required",(await store.CreateAsync(f.Book.Id,request,f.Actor,default)).ErrorCode);
    });
}
