using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Tests;
public sealed class CopyConcurrencyTests
{
    [LocalMongoFact]
    public Task Expiration_competing_with_collection_releases_the_assigned_unit()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("RACE-EXP","good",null,null,"one"),f.Actor,default)).Copy!;
        var store=new MongoCirculationStore(f.Db,Options.Create(new CirculationOptions{Mode="active"}));
        var pickup=(await store.ReserveForPickupAsync(reader.Id,f.Book.Id,"reserve",default)).Reservation!;
        await f.Db.PickupReservations.UpdateOneAsync(p=>p.Id==pickup.Id,Builders<PickupReservation>.Update.Set(p=>p.PickupExpiresAt,DateTime.UtcNow.AddMinutes(-1)));
        var collection=store.CollectPickupReservationAsync(pickup.Id,f.Actor.UserId,1,default,new(copy.Id,2));
        var expiration=store.ProcessExpiredPickupReservationsAsync(DateTime.UtcNow,default);
        await Task.WhenAll(collection,expiration);
        Assert.False(collection.Result.Success);Assert.Equal(1,expiration.Result);
        Assert.Equal("expired",(await f.Db.PickupReservations.Find(p=>p.Id==pickup.Id).SingleAsync()).Status);
        Assert.True((await CirculationMaintenance.RunAsync(f.Db,"active",false,default)).Books.Single().Consistent);
    });

    [LocalMongoFact]
    public Task Cancellation_offers_the_same_unit_to_the_first_waiting_reader()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var next=await SupportPersistenceTests.UserAsync(f.Db,"user");var last=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("RACE-FIFO","good",null,null,"one"),f.Actor,default)).Copy!;
        var store=new MongoCirculationStore(f.Db,Options.Create(new CirculationOptions{Mode="active"}));
        var pickup=(await store.ReserveForPickupAsync(reader.Id,f.Book.Id,"reserve",default)).Reservation!;
        var first=(await store.JoinWaitlistAsync(next.Id,f.Book.Id,default)).Entry!;
        await store.JoinWaitlistAsync(last.Id,f.Book.Id,default);
        Assert.True(await store.CancelPickupReservationAsync(pickup.Id,reader.Id,false,null,1,default));
        var offered=await f.Db.PickupReservations.Find(p=>p.Status=="ready").SingleAsync();
        Assert.Equal(first.Id,offered.WaitlistEntryId);Assert.Equal(copy.Id,offered.CopyId);
        Assert.True((await CirculationMaintenance.RunAsync(f.Db,"active",false,default)).Books.Single().Consistent);
    });
    [LocalMongoFact]
    public Task Loss_competing_with_return_has_one_resolution_and_conserves_stock()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("RACE-1","good",null,null,"one"),f.Actor,default)).Copy!;
        var store=new MongoCirculationStore(f.Db,Options.Create(new CirculationOptions{Mode="active"}));
        var p=(await store.ReserveForPickupAsync(reader.Id,f.Book.Id,"r",default)).Reservation!;
        var loan=(await store.CollectPickupReservationAsync(p.Id,f.Actor.UserId,1,default,new(copy.Id,2))).Loan!;
        var loss=new CopyTransitions(f.Db,TimeProvider.System).ChangeStatusAsync(copy.Id,new("lost","Missing",3,"lost"),f.Actor,default);
        var returned=store.ReturnPhysicalLoanAsync(loan.Id,f.Actor.UserId,default,request:new(new(copy.Id,3),new("good",null,null),"return"));
        await Task.WhenAll(loss,returned);
        Assert.NotEqual(loss.Result.Success,returned.Result);
        var unit=await db.GetCollection<BookCopy>("BookCopies").Find(c=>c.Id==copy.Id).SingleAsync();
        var book=await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync();
        Assert.Equal(1,book.TotalCopies);Assert.Equal(returned.Result?1:0,book.AvailableCopies);Assert.Null(unit.ActiveAssignmentId);
        Assert.Equal(returned.Result?"available":"lost",unit.Status);
    });

    [LocalMongoFact]
    public Task Legacy_mutations_cannot_bypass_unit_inventory()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("RACE-2","good",null,null,"one"),f.Actor,default)).Copy!;
        var legacy=new MongoLoanStore(f.Db);
        Assert.Null(await legacy.ReserveAvailableBookAsync(f.Book.Id,MongoDB.Bson.ObjectId.GenerateNewId().ToString()));
        Assert.False(await legacy.RestoreBookAvailabilityAsync(f.Book.Id,MongoDB.Bson.ObjectId.GenerateNewId().ToString(),true));
        var store=new MongoCirculationStore(f.Db,Options.Create(new CirculationOptions{Mode="active"}));
        var p=(await store.ReserveForPickupAsync(reader.Id,f.Book.Id,"r",default)).Reservation!;
        var loan=(await store.CollectPickupReservationAsync(p.Id,f.Actor.UserId,1,default,new(copy.Id,2))).Loan!;
        Assert.False(await legacy.CompletePhysicalAsync(loan.Id,f.Book.Id,"returned",DateTime.UtcNow,default));
        Assert.False(await legacy.MarkReturnedAsync(loan.Id,DateTime.UtcNow));
        Assert.False(await legacy.DeleteLoanAsync(loan.Id));
        Assert.False(await legacy.TransitionAsync(loan.Id,["active"],"returned",DateTime.UtcNow,default));
    });
}
