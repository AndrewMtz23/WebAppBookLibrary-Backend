using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Domain.Copies;

namespace WebAppBookLibrary.Services.Copies;

internal sealed class CopyConflict(string code) : Exception(code);

internal sealed class CopyContext(MongoDBService mongo, TimeProvider clock)
{
    internal IMongoDatabase Db => mongo._database;
    internal IMongoCollection<BookCopy> Copies => Db.GetCollection<BookCopy>("BookCopies");
    internal IMongoCollection<CopyHistoryEntry> History => Db.GetCollection<CopyHistoryEntry>("CopyHistory");
    internal DateTime Now => new BsonDateTime(clock.GetUtcNow().UtcDateTime).ToUniversalTime();

    internal async Task<CopyMutationResult> Transact(CopyActor actor, Func<IClientSessionHandle,User,CancellationToken,Task<CopyMutationResult>> action, CancellationToken ct)
    {
        if (!ObjectId.TryParse(actor.UserId,out _)) return new(false,"copy_forbidden");
        try
        {
            using var session=await Db.Client.StartSessionAsync(cancellationToken:ct);
            return await session.WithTransactionAsync(async(tx,token)=>
            {
                var user=await mongo.Users.Find(tx,u=>u.Id==actor.UserId && u.IsActive).FirstOrDefaultAsync(token);
                if(user is null || user.Role is not ("admin" or "librarian")) throw new CopyConflict("copy_forbidden");
                await UserReferenceGuard.TouchAsync(mongo.Users,tx,user.Id,token);
                return await action(tx,user,token);
            },new TransactionOptions(readConcern:ReadConcern.Snapshot,writeConcern:WriteConcern.WMajority),ct);
        }
        catch(CopyConflict ex) { return new(false,ex.Message); }
        catch(UserReferenceUnavailableException) { return new(false,"copy_forbidden"); }
        catch(MongoException ex) when(ex is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } || ex is MongoCommandException { Code:11000 })
        { return new(false,"copy_duplicate"); }
    }

    internal async Task<Book> LockBookAsync(IClientSessionHandle tx,string bookId,CancellationToken ct)
    {
        var book=await mongo.Books.FindOneAndUpdateAsync(tx,Builders<Book>.Filter.Eq(b=>b.Id,bookId),
            Builders<Book>.Update.Inc(b=>b.ReferenceVersion,1).Inc(b=>b.InventoryVersion,1),
            new FindOneAndUpdateOptions<Book,Book>{ReturnDocument=ReturnDocument.After},ct);
        if(book is null) throw new CopyConflict("book_not_found");
        if(book.MediaType!="physical" || book.InventoryMode!="unit") throw new CopyConflict("copy_conversion_required");
        return book;
    }

    internal async Task RefreshProjectionAsync(IClientSessionHandle tx,string bookId,CancellationToken ct)
    {
        var copies=await Copies.Find(tx,c=>c.BookId==bookId).ToListAsync(ct);
        if(copies.Any(c=>c.Status=="available"&&!CopyRules.IsLendable(c))) throw new CopyConflict("inventory_reconciliation_required");
        var available=copies.Count(CopyRules.IsLendable);
        await mongo.Books.UpdateOneAsync(tx,b=>b.Id==bookId,Builders<Book>.Update
            .Set(b=>b.TotalCopies,copies.Count(c=>c.Status!="retired"))
            .Set(b=>b.AvailableCopies,available).Set(b=>b.RetainedCopies,copies.Count(c=>c.Status=="held"))
            .Set(b=>b.IsAvailable,available>0).Set(b=>b.UpdatedAt,Now),cancellationToken:ct);
    }

    internal Task AuditAsync(IClientSessionHandle tx,BookCopy copy,string action,string? from,string? actor,string? reason,CancellationToken ct)
        => History.InsertOneAsync(tx,new CopyHistoryEntry { Id=ObjectId.GenerateNewId().ToString(),CopyId=copy.Id,BookId=copy.BookId,
            Action=action,FromStatus=from,ToStatus=copy.Status,ActorId=actor,Reason=reason,CreatedAt=Now },cancellationToken:ct);
}
