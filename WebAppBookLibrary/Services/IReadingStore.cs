using WebAppBookLibrary.Contracts.Reading;

namespace WebAppBookLibrary.Services;

public sealed record ReadingMutationResult(int Status, string? ErrorCode = null, ReadingResponse? Entry = null);

public interface IReadingStore
{
    Task<ReadingListResponse> ListAsync(string userId, ReadingQuery query, CancellationToken ct);
    Task<ReadingResponse?> LatestAsync(string userId, CancellationToken ct);
    Task<ReadingMutationResult> SaveAsync(string userId, string bookId, SaveReadingRequest request, DateTime now, CancellationToken ct);
    Task<ReadingMutationResult> DeleteAsync(string userId, string bookId, string expectedRevision, CancellationToken ct);
}
