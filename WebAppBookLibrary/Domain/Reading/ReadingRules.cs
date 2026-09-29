using WebAppBookLibrary.Contracts.Reading;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Domain.Reading;

public sealed record ReadingRuleResult(ReadingEntry? Entry, string? ErrorCode, bool Changed);

public static class ReadingRules
{
    public static bool IsStatus(string? status) => status is "want_to_read" or "reading" or "finished";

    public static ReadingRuleResult Apply(ReadingEntry? current, SaveReadingRequest request, int? catalogPageCount, DateTime now)
    {
        if (!IsStatus(request.Status) || request.ProgressMode is not ("percent" or "page")) return Invalid();
        int? total = null, page = null;
        int percent;
        if (request.ProgressMode == "page")
        {
            total = request.AdoptCurrentPageCount || current?.ProgressMode != "page" ? catalogPageCount : current.PageCountSnapshot;
            page = request.CurrentPage;
            if (total is null or <= 0 || page is null or < 0 || page > total || request.ProgressPercent is not null) return Invalid();
            percent = (int)((long)page.Value * 100 / total.Value);
        }
        else
        {
            if (request.ProgressPercent is null or < 0 or > 100 || request.CurrentPage is not null || request.AdoptCurrentPageCount) return Invalid();
            percent = request.ProgressPercent.Value;
        }
        if (current is not null && ((current.Status != "want_to_read" && request.Status == "want_to_read") ||
            (current.Status == "finished" && request.Status == "reading")))
        {
            if (!request.ConfirmReset) return new(null, "reading_confirmation_required", false);
            if (request.Status == "reading" && percent == 100) return Invalid();
        }
        var status = request.Status;
        if (current?.Status == "finished" && status == "finished" && request.AdoptCurrentPageCount && page != total) return Invalid();
        if (status == "want_to_read") { percent = 0; if (page is not null) page = 0; }
        else if (status == "finished") { percent = 100; if (page is not null) page = total; }
        else if (percent == 100) status = "finished";
        // A derived 100% after changing only the snapshot is not new reading activity.
        var activityChanged = current is null || current.Status != request.Status ||
            (current.ProgressMode == request.ProgressMode && request.ProgressMode == "page" ? current.CurrentPage != page : current.ProgressPercent != percent);
        var entry = (current ?? new ReadingEntry()) with {
            Status = status, ProgressMode = request.ProgressMode, ProgressPercent = percent,
            CurrentPage = page, PageCountSnapshot = total,
            StartedAt = status == "want_to_read" ? null : current?.StartedAt ?? now,
            FinishedAt = status == "finished" ? current?.FinishedAt ?? now : null,
            LastProgressAt = status == "want_to_read" ? null : activityChanged ? now : current?.LastProgressAt
        };
        var changed = current is null || entry != current;
        if (changed) entry = entry with { UpdatedAt = now };
        return new(entry, null, changed);
    }
    private static ReadingRuleResult Invalid() => new(null, "reading_invalid", false);
}
