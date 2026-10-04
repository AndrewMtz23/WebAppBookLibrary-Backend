using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class NotificationReminders(MongoDBService mongo, TimeProvider clock, IOptions<NotificationOptions> options)
{
    public static string? CurrentType(Loan loan, DateTime now, NotificationOptions options)
    {
        var current = LoanResponse.From(loan, now);
        if (current.MediaType != "physical" || current.Status is not ("active" or "overdue") || loan.IsReturned || current.DueAt is null) return null;
        var hours = (current.DueAt.Value - now).TotalHours;
        return hours <= 0 ? "due_overdue" : hours <= options.SoonHours ? "due_soon" : hours <= options.EarlyHours ? "due_early" : null;
    }

    public async Task ProcessBatchAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return;
        var now = clock.GetUtcNow().UtcDateTime;
        await NotificationMetrics.SampleAsync(mongo._database, now, ct);
        var state = mongo._database.GetCollection<BsonDocument>("NotificationWorkerState");
        await state.UpdateOneAsync(new BsonDocument("_id", "reminders"), new BsonDocument("$setOnInsert", new BsonDocument { { "Cursor", "" }, { "LeaseUntil", DateTime.MinValue } }), new UpdateOptions { IsUpsert = true }, ct);
        var lease = Guid.NewGuid().ToString("N");
        var owned = await state.FindOneAndUpdateAsync(new BsonDocument { { "_id", "reminders" }, { "LeaseUntil", new BsonDocument("$lte", now) } },
            new BsonDocument("$set", new BsonDocument { { "LeaseId", lease }, { "LeaseUntil", now.AddMinutes(2) } }),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        if (owned is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var cursor = owned["Cursor"].AsString;
        // Bound each pass, including old/legacy records. The durable cursor advances over all loans.
        var filter = cursor.Length == 0 ? FilterDefinition<Loan>.Empty : Builders<Loan>.Filter.Gt(l => l.Id, cursor);
        var loans = await mongo.Loans.Find(filter).SortBy(l => l.Id).Limit(100).ToListAsync(token);
        foreach (var candidate in loans)
        {
            using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: token);
            await session.WithTransactionAsync(async (tx, cancellation) => {
                var loan = await mongo.Loans.Find(tx, l => l.Id == candidate.Id).FirstOrDefaultAsync(cancellation);
                if (loan is null) return false;
                var type = CurrentType(loan, now, options.Value);
                if (type is null) return false;
                var account = await mongo.Users.Find(tx, u => u.Id == loan.UserId && u.IsActive).FirstOrDefaultAsync(cancellation);
                if (account is null) return false;
                loan.DueAt = LoanResponse.From(loan, now).DueAt;
                var key = loan.Id + ":" + type + ":" + loan.DueAt?.Ticks;
                if (await mongo._database.GetCollection<Notification>("Notifications").Find(tx, n => n.UserId == loan.UserId && n.EventKey == key).AnyAsync(cancellation)) return false;
                // Force a write conflict with returns, cancellations and due-date changes.
                await mongo.Loans.UpdateOneAsync(tx, l => l.Id == loan.Id, Builders<Loan>.Update.Inc(l => l.NotificationVersion, 1), cancellationToken: cancellation);
                await UserReferenceGuard.TouchAsync(mongo.Users, tx, loan.UserId, cancellation);
                await NotificationEvents.AppendAsync(mongo._database, tx, loan, type, now, cancellation);
                return true;
            }, cancellationToken: token);
        }
        await state.UpdateOneAsync(new BsonDocument { { "_id", "reminders" }, { "LeaseId", lease } },
            new BsonDocument("$set", new BsonDocument { { "Cursor", loans.Count < 100 ? "" : loans[^1].Id }, { "LeaseUntil", now } }), cancellationToken: token);
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopes, IOptions<NotificationOptions> options, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<NotificationReminders>().ProcessBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Notification reminder pass unavailable; durable cursor retained."); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken);
        }
    }
}
