using System.Text.Json.Serialization;

namespace WebAppBookLibrary.Contracts.Reading;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveReadingRequest(string Status, string ProgressMode, int? ProgressPercent, int? CurrentPage,
    string? ExpectedRevision, bool ConfirmReset = false, bool AdoptCurrentPageCount = false);

public sealed class ReadingQuery
{
    public string? Status { get; init; }
    public string? BookId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public sealed record ReadingResponse(string BookId, string Revision, string Status, string ProgressMode,
    int ProgressPercent, int? CurrentPage, int? PageCountSnapshot, int? CurrentPageCount, bool PageCountChanged,
    DateTime? StartedAt, DateTime? FinishedAt, DateTime? LastProgressAt, DateTime UpdatedAt,
    bool BookAvailable, string Title, string? CoverUrl);
public sealed record ReadingCounts(long WantToRead, long Reading, long Finished);
public sealed record ReadingListResponse(IReadOnlyList<ReadingResponse> Items, int Page, int PageSize, long TotalItems, ReadingCounts Counts);
public sealed record LatestReadingResponse(ReadingResponse? Entry);
