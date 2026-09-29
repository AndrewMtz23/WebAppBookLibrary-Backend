using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Reading;
using WebAppBookLibrary.Domain.Reading;

namespace WebAppBookLibrary.Services;

public sealed class ReadingService(IReadingStore store, MongoDBService db, TimeProvider clock)
{
    public Task<bool> IsActiveAsync(string userId, CancellationToken ct) => ObjectId.TryParse(userId, out _)
        ? db.Users.Find(u => u.Id == userId && u.IsActive).AnyAsync(ct) : Task.FromResult(false);
    public static bool ValidQuery(ReadingQuery query) => query.Page > 0 && query.PageSize is > 0 and <= 50 &&
        (query.Status is null || ReadingRules.IsStatus(query.Status)) && (query.BookId is null || ObjectId.TryParse(query.BookId, out _));
    public Task<ReadingListResponse> ListAsync(string userId, ReadingQuery query, CancellationToken ct) => store.ListAsync(userId, query, ct);
    public Task<ReadingResponse?> LatestAsync(string userId, CancellationToken ct) => store.LatestAsync(userId, ct);
    public Task<ReadingMutationResult> SaveAsync(string userId, string bookId, SaveReadingRequest request, CancellationToken ct) =>
        ObjectId.TryParse(bookId, out _) ? store.SaveAsync(userId, bookId, request, clock.GetUtcNow().UtcDateTime, ct) : Task.FromResult(new ReadingMutationResult(400, "reading_invalid"));
    public Task<ReadingMutationResult> DeleteAsync(string userId, string bookId, string revision, CancellationToken ct) =>
        ObjectId.TryParse(bookId, out _) ? store.DeleteAsync(userId, bookId, revision, ct) : Task.FromResult(new ReadingMutationResult(400, "reading_invalid"));
}
