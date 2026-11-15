using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Branches;

public sealed class BranchCatalogStore(MongoDBService mongo, TimeProvider clock)
{
    public async Task<BranchResult<BranchCatalogEntry>> SetAsync(string actor, string branchId, string bookId, bool active, long expectedVersion, CancellationToken ct)
    {
        if (!ObjectId.TryParse(bookId, out _) || expectedVersion < 0) return new(false, "catalog_invalid");
        try
        {
            using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
            var value = await session.WithTransactionAsync(async (tx, token) =>
            {
                await new BranchAccessService(mongo).ValidateForMutationAsync(tx, actor, branchId, token);
                var book = await mongo.Books.FindOneAndUpdateAsync(tx, b => b.Id == bookId,
                    Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1), new() { ReturnDocument = ReturnDocument.After }, token);
                if (book is null) throw new BranchAccessException("book_not_found");
                if (active && !book.IsActive)
                    await mongo.Books.UpdateOneAsync(tx, b => b.Id == bookId, Builders<Book>.Update.Set(b => b.IsActive, true), cancellationToken: token);
                var collection = mongo._database.GetCollection<BranchCatalogEntry>("BranchCatalog");
                var entry = await collection.Find(tx, e => e.BookId == bookId && e.BranchId == branchId).FirstOrDefaultAsync(token);
                if ((entry?.Version ?? 0) != expectedVersion) throw new BranchAccessException("version_conflict");
                if (!active)
                {
                    if (await mongo._database.GetCollection<BookCopy>("BookCopies").Find(tx, c => c.BranchId == branchId && c.BookId == bookId && c.Status != "retired").AnyAsync(token))
                        throw new BranchAccessException("catalog_has_inventory");
                    if (book.InventoryMode != "unit" && book.InventoryBranchId == branchId && book.MediaType == "physical" && book.TotalCopies > 0)
                        throw new BranchAccessException("catalog_has_inventory");
                    if (await new BranchStore(mongo, clock).HasCommitmentsAsync(tx, branchId, token, bookId)) throw new BranchAccessException("branch_has_commitments");
                }
                var now = new BsonDateTime(clock.GetUtcNow().UtcDateTime).ToUniversalTime();
                if (entry is null)
                {
                    entry = new() { Id = branchId + ":" + bookId, BranchId = branchId, BookId = bookId, IsActive = active, UpdatedAt = now };
                    await collection.InsertOneAsync(tx, entry, cancellationToken: token);
                }
                else
                {
                    entry.IsActive = active; entry.Version++; entry.UpdatedAt = now;
                    await collection.ReplaceOneAsync(tx, e => e.Id == entry.Id, entry, cancellationToken: token);
                }
                await mongo.LogEntries.InsertOneAsync(tx, new() { Id = ObjectId.GenerateNewId().ToString(), Timestamp = now, EventType = "branch_catalog_updated", Level = "Information", ActorId = actor, TargetType = "book", TargetId = bookId, Metadata = new() { ["branchId"] = branchId, ["active"] = active.ToString() } }, cancellationToken: token);
                return entry;
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct);
            return new(true, null, value);
        }
        catch (BranchAccessException ex) { return new(false, ex.Code); }
        catch (UserReferenceUnavailableException) { return new(false, "branch_forbidden"); }
        catch (MongoException ex) when (ex is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } or MongoCommandException { Code: 11000 }) { return new(false, "version_conflict"); }
    }
}
