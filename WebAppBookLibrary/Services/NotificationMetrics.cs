using System.Diagnostics.Metrics;
using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public static class NotificationMetrics
{
    private static readonly Meter Meter = new("BookLibrary.Notifications", "1.0.0");
    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("booklibrary.notifications.delivery.attempts");
    private static readonly Histogram<double> Backlog = Meter.CreateHistogram<double>("booklibrary.notifications.pending", "{message}");
    private static readonly Histogram<double> Age = Meter.CreateHistogram<double>("booklibrary.notifications.oldest_pending", "s");
    private static readonly Histogram<double> Dead = Meter.CreateHistogram<double>("booklibrary.notifications.dead", "{message}");
    public static void Delivery(string outcome) => Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public static async Task SampleAsync(IMongoDatabase db, DateTime now, CancellationToken ct)
    {
        var jobs = db.GetCollection<AccountMailJob>("AccountMailOutbox");
        var filter = Builders<AccountMailJob>.Filter.Ne(j => j.NotificationId, null) & Builders<AccountMailJob>.Filter.Eq(j => j.Stage, "delivery");
        var count = await jobs.CountDocumentsAsync(filter, cancellationToken: ct);
        var oldest = await jobs.Find(filter).SortBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        Backlog.Record(count); Age.Record(oldest is null ? 0 : Math.Max(0, (now - oldest.CreatedAt).TotalSeconds));
        Dead.Record(await jobs.CountDocumentsAsync(j => j.NotificationId != null && j.Stage == "dead", cancellationToken: ct));
    }
}
