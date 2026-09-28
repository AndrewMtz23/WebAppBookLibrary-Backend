using WebAppBookLibrary.Contracts.Reading;
using WebAppBookLibrary.Domain.Reading;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Tests;

public class ReadingRulesTests
{
    private static readonly DateTime Now = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static SaveReadingRequest Request(string status = "reading", int percent = 25) => new(status, "percent", percent, null, null, false, false);
    private static ReadingEntry Apply(ReadingEntry? entry, SaveReadingRequest request, int? pages = 1000) =>
        Assert.IsType<ReadingEntry>(ReadingRules.Apply(entry, request, pages, Now).Entry);

    [Theory]
    [InlineData(999, 99, "reading")]
    [InlineData(1000, 100, "finished")]
    [InlineData(0, 0, "reading")]
    public void Pages_only_finish_at_actual_total(int page, int percent, string status)
    {
        var result = Apply(null, new("reading", "page", null, page, null, false, false));
        Assert.Equal(percent, result.ProgressPercent); Assert.Equal(status, result.Status);
        Assert.Equal(Now, result.StartedAt); Assert.Equal(Now, result.LastProgressAt);
        Assert.Equal(page == 1000 ? Now : (DateTime?)null, result.FinishedAt);
    }

    [Theory]
    [InlineData(-1)] [InlineData(101)]
    public void Out_of_range_percent_is_rejected(int value) => Assert.Equal("reading_invalid", ReadingRules.Apply(null, Request(percent: value), 100, Now).ErrorCode);

    [Fact]
    public void Invalid_modes_pages_and_inconsistent_values_are_rejected()
    {
        foreach (var request in new[] { Request() with { Status = "other" }, Request() with { ProgressMode = "other" }, Request() with { CurrentPage = 1 }, new SaveReadingRequest("reading", "page", null, -1, null, false, false), new("reading", "page", null, 101, null, false, false) })
            Assert.Equal("reading_invalid", ReadingRules.Apply(null, request, 100, Now).ErrorCode);
        foreach (var total in new int?[] { null, 0, -1 })
            Assert.Equal("reading_invalid", ReadingRules.Apply(null, new("reading", "page", null, 0, null, false, false), total, Now).ErrorCode);
    }

    [Fact]
    public void Restart_and_reopen_require_confirmation_and_clean_dates()
    {
        var entry = Apply(null, Request("finished", 100));
        Assert.Equal("reading_confirmation_required", ReadingRules.Apply(entry, Request(), 100, Now).ErrorCode);
        var reopened = Apply(entry, Request() with { ConfirmReset = true });
        Assert.Equal(entry.StartedAt, reopened.StartedAt); Assert.Null(reopened.FinishedAt);
        Assert.Equal("reading_confirmation_required", ReadingRules.Apply(reopened, Request("want_to_read", 0), 100, Now).ErrorCode);
        var reset = Apply(reopened, Request("want_to_read", 0) with { ConfirmReset = true });
        Assert.Equal(0, reset.ProgressPercent); Assert.Null(reset.StartedAt); Assert.Null(reset.FinishedAt); Assert.Null(reset.LastProgressAt);
    }

    [Fact]
    public void Repetition_does_not_invent_activity_and_correction_preserves_start()
    {
        var entry = Apply(null, Request());
        var repeated = ReadingRules.Apply(entry, Request(), 1000, Now.AddDays(1));
        Assert.False(repeated.Changed); Assert.Equal(Now, repeated.Entry!.LastProgressAt);
        var corrected = ReadingRules.Apply(entry, Request(percent: 10), 1000, Now.AddDays(1)).Entry!;
        Assert.Equal(Now, corrected.StartedAt); Assert.Equal(Now.AddDays(1), corrected.LastProgressAt);
        Assert.Equal(25, entry.ProgressPercent); // Pure rule must not mutate input.
    }

    [Fact]
    public void Page_snapshot_is_explicit_and_metadata_alone_does_not_update_activity()
    {
        var request = new SaveReadingRequest("reading", "page", null, 20, null, false, false);
        var entry = Apply(null, request, 100);
        Assert.Equal(100, Apply(entry, request, 200).PageCountSnapshot);
        Assert.Equal(100, Apply(entry, request, null).PageCountSnapshot);
        var adopted = ReadingRules.Apply(entry, request with { AdoptCurrentPageCount = true }, 200, Now.AddDays(1)).Entry!;
        Assert.Equal(200, adopted.PageCountSnapshot); Assert.Equal(10, adopted.ProgressPercent); Assert.Equal(Now, adopted.LastProgressAt);
        Assert.Equal("reading_invalid", ReadingRules.Apply(entry, request with { AdoptCurrentPageCount = true }, 10, Now).ErrorCode);
        Assert.Equal("reading_invalid", ReadingRules.Apply(entry, request with { AdoptCurrentPageCount = true }, null, Now).ErrorCode);
        var corrected = Apply(entry, request with { CurrentPage = 5, AdoptCurrentPageCount = true }, 10);
        Assert.Equal(50, corrected.ProgressPercent);
        var percent = Apply(entry, Request(percent: 20));
        Assert.Null(percent.CurrentPage); Assert.Null(percent.PageCountSnapshot);
        Assert.Equal("reading_invalid", ReadingRules.Apply(percent, request with { CurrentPage = null }, 100, Now).ErrorCode);
    }
}
