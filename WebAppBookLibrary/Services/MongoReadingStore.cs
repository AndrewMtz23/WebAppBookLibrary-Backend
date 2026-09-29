using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Reading;
using WebAppBookLibrary.Domain.Reading;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class MongoReadingStore(MongoDBService db) : IReadingStore
{
    private IMongoCollection<ReadingEntry> Entries => db.ReadingEntries;
    private static readonly TransactionOptions Snapshot = new(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority);
    public static bool ValidRevision(string? revision)
    {
        var parts = revision?.Split(':');
        return parts?.Length == 2 && ObjectId.TryParse(parts[0], out _) &&
            long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0;
    }
    private static string Revision(ReadingEntry entry) => $"{entry.Id}:{entry.Version}";
    private static ReadingResponse Project(ReadingEntry entry, Book? book)
    {
        var available = book?.IsActive == true;
        return new(entry.BookId, Revision(entry), entry.Status, entry.ProgressMode, entry.ProgressPercent,
            entry.CurrentPage, entry.PageCountSnapshot, available ? book!.PageCount : null,
            available && entry.ProgressMode == "page" && entry.PageCountSnapshot != book!.PageCount,
            entry.StartedAt, entry.FinishedAt, entry.LastProgressAt, entry.UpdatedAt,
            available, available ? book!.Title : "Libro no disponible", available ? book!.CoverUrl : null);
    }
    public static Task CreateIndexesAsync(IMongoCollection<ReadingEntry> entries)
    {
        var k = Builders<ReadingEntry>.IndexKeys;
        return entries.Indexes.CreateManyAsync([
            new(k.Ascending(e => e.UserId).Ascending(e => e.BookId), new() { Name = "ux_reading_user_book", Unique = true }),
            new(k.Ascending(e => e.UserId).Ascending(e => e.Status).Descending(e => e.UpdatedAt).Descending(e => e.Id), new() { Name = "ix_reading_status_updated" }),
            new(k.Ascending(e => e.UserId).Descending(e => e.UpdatedAt).Descending(e => e.Id), new() { Name = "ix_reading_updated" }),
            new(k.Ascending(e => e.UserId).Ascending(e => e.Status).Descending(e => e.LastProgressAt).Descending(e => e.Id), new() { Name = "ix_reading_activity" })
        ]);
    }

    public async Task<ReadingListResponse> ListAsync(string userId, ReadingQuery query, CancellationToken ct)
    {
        using var session = await db._database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (s, token) => {
            var f = Builders<ReadingEntry>.Filter;
            var owner = f.Eq(e => e.UserId, userId);
            var filter = owner;
            if (query.Status is not null) filter &= f.Eq(e => e.Status, query.Status);
            if (query.BookId is not null) filter &= f.Eq(e => e.BookId, query.BookId);
            var counts = new ReadingCounts(
                await Entries.CountDocumentsAsync(s, owner & f.Eq(e => e.Status, "want_to_read"), cancellationToken: token),
                await Entries.CountDocumentsAsync(s, owner & f.Eq(e => e.Status, "reading"), cancellationToken: token),
                await Entries.CountDocumentsAsync(s, owner & f.Eq(e => e.Status, "finished"), cancellationToken: token));
            var total = await Entries.CountDocumentsAsync(s, filter, cancellationToken: token);
            var offset = (long)(query.Page - 1) * query.PageSize;
            var rows = offset > int.MaxValue ? [] : await Entries.Find(s, filter)
                .SortByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Id).Skip((int)offset).Limit(query.PageSize).ToListAsync(token);
            var ids = rows.Select(e => e.BookId).ToArray();
            var books = ids.Length == 0 ? [] : await db.Books.Find(s, Builders<Book>.Filter.In(b => b.Id, ids) & Builders<Book>.Filter.Eq(b => b.IsActive, true)).ToListAsync(token);
            var byId = books.ToDictionary(b => b.Id);
            return new ReadingListResponse(rows.Select(e => Project(e, byId.GetValueOrDefault(e.BookId))).ToArray(), query.Page, query.PageSize, total, counts);
        }, Snapshot, ct);
    }

    public async Task<ReadingResponse?> LatestAsync(string userId, CancellationToken ct)
    {
        using var session = await db._database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (s, token) => {
            // Filter unavailable books before limit; one aggregation regardless of history size.
            var pipeline = new BsonDocument[] {
                new("$match", new BsonDocument { { "UserId", ObjectId.Parse(userId) }, { "Status", "reading" }, { "LastProgressAt", new BsonDocument("$ne", BsonNull.Value) } }),
                new("$sort", new BsonDocument { { "LastProgressAt", -1 }, { "_id", -1 } }),
                new("$lookup", new BsonDocument { { "from", "Books" }, { "localField", "BookId" }, { "foreignField", "_id" }, { "as", "visibleBook" } }),
                new("$match", new BsonDocument("visibleBook", new BsonDocument("$elemMatch", new BsonDocument("IsActive", true)))),
                new("$limit", 1), new("$project", new BsonDocument("visibleBook", 0))
            };
            var row = await Entries.Aggregate<ReadingEntry>(s, pipeline).FirstOrDefaultAsync(token);
            if (row is null) return null;
            var book = await db.Books.Find(s, b => b.Id == row.BookId && b.IsActive).FirstOrDefaultAsync(token);
            return Project(row, book);
        }, Snapshot, ct);
    }

    public async Task<ReadingMutationResult> SaveAsync(string userId, string bookId, SaveReadingRequest request, DateTime now, CancellationToken ct)
    {
        if (request.ExpectedRevision is not null && !ValidRevision(request.ExpectedRevision)) return new(400, "reading_invalid");
        using var session = await db._database.Client.StartSessionAsync(cancellationToken: ct);
        try {
            return await session.WithTransactionAsync(async (s, token) => {
                await UserReferenceGuard.TouchAsync(db.Users, s, userId, token);
                var book = await db.Books.FindOneAndUpdateAsync(s, b => b.Id == bookId && b.IsActive,
                    Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1), new() { ReturnDocument = ReturnDocument.After }, token);
                if (book is null) return new ReadingMutationResult(404, "reading_book_unavailable");
                var current = await Entries.Find(s, e => e.UserId == userId && e.BookId == bookId).FirstOrDefaultAsync(token);
                if ((current is null && request.ExpectedRevision is not null) || (current is not null && request.ExpectedRevision != Revision(current)))
                    return new ReadingMutationResult(409, "reading_conflict");
                var result = ReadingRules.Apply(current, request, book.PageCount, now);
                if (result.ErrorCode is not null) return new ReadingMutationResult(400, result.ErrorCode);
                if (!result.Changed) return new ReadingMutationResult(200, Entry: Project(current!, book));
                var entry = result.Entry! with { Id = current?.Id ?? ObjectId.GenerateNewId().ToString(), UserId = userId, BookId = bookId, Version = current is null ? 1 : checked(current.Version + 1) };
                if (current is null) await Entries.InsertOneAsync(s, entry, cancellationToken: token);
                else await Entries.ReplaceOneAsync(s, e => e.Id == current.Id && e.Version == current.Version, entry, cancellationToken: token);
                await Audit(s, "reading_saved", userId, entry.Id, token);
                return new ReadingMutationResult(200, Entry: Project(entry, book));
            }, Snapshot, ct);
        }
        catch (UserReferenceUnavailableException) { return new(401, "reading_account_unavailable"); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return new(409, "reading_conflict"); }
    }

    public async Task<ReadingMutationResult> DeleteAsync(string userId, string bookId, string expectedRevision, CancellationToken ct)
    {
        if (!ValidRevision(expectedRevision)) return new(400, "reading_invalid");
        using var session = await db._database.Client.StartSessionAsync(cancellationToken: ct);
        try {
            return await session.WithTransactionAsync(async (s, token) => {
                await UserReferenceGuard.TouchAsync(db.Users, s, userId, token);
                var entry = await Entries.Find(s, e => e.UserId == userId && e.BookId == bookId).FirstOrDefaultAsync(token);
                if (entry is null) return new ReadingMutationResult(204);
                if (Revision(entry) != expectedRevision) return new ReadingMutationResult(409, "reading_conflict");
                await Entries.DeleteOneAsync(s, e => e.Id == entry.Id && e.Version == entry.Version, cancellationToken: token);
                await Audit(s, "reading_removed", userId, entry.Id, token);
                return new ReadingMutationResult(204);
            }, Snapshot, ct);
        } catch (UserReferenceUnavailableException) { return new(401, "reading_account_unavailable"); }
    }
    private Task Audit(IClientSessionHandle session, string action, string actor, string target, CancellationToken ct) =>
        db.LogEntries.InsertOneAsync(session, AuditLogEntryFactory.UserChanged(action, actor, target, new Dictionary<string, string> { ["operation"] = "reading" }, null), cancellationToken: ct);
}
