using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Domain.Circulation;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class CirculationReminders(
    MongoDBService mongo,
    ICirculationStore circulationStore,
    TimeProvider clock)
{
    public async Task ProcessBatchAsync(CancellationToken ct)
    {
        if (await circulationStore.GetModeAsync(ct) == CirculationModes.Legacy) return;

        var now = clock.GetUtcNow().UtcDateTime;
        var state = mongo._database.GetCollection<BsonDocument>("CirculationWorkerState");
        await state.UpdateOneAsync(
            new BsonDocument("_id", "expirations"),
            new BsonDocument("$setOnInsert", new BsonDocument { { "LeaseUntil", DateTime.MinValue } }),
            new UpdateOptions { IsUpsert = true },
            ct);

        var lease = Guid.NewGuid().ToString("N");
        var owned = await state.FindOneAndUpdateAsync(
            new BsonDocument { { "_id", "expirations" }, { "LeaseUntil", new BsonDocument("$lte", now) } },
            new BsonDocument("$set", new BsonDocument { { "LeaseId", lease }, { "LeaseUntil", now.AddMinutes(2) } }),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            ct);

        if (owned is null) return;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;

        try
        {
            // 1. Process expired pickup reservations
            CirculationTelemetry.Expired.Add(await circulationStore.ProcessExpiredPickupReservationsAsync(now, token));
            await circulationStore.PromoteAvailableAsync(token);
            var oldest = await mongo.WaitlistEntries.Find(w => w.Status == "queued").SortBy(w => w.CreatedAt).FirstOrDefaultAsync(token);
            if (oldest != null) CirculationTelemetry.QueueAge.Record(Math.Max(0, (now - oldest.CreatedAt).TotalSeconds));

            // Persist bounded scans so a large healthy prefix cannot starve later rows.
            var pickupCursor = owned.GetValue("PickupCursor", "").AsString;
            var pickupFilter = Builders<PickupReservation>.Filter.Eq(p => p.Status, "ready");
            if (pickupCursor.Length > 0) pickupFilter &= Builders<PickupReservation>.Filter.Gt(p => p.Id, pickupCursor);
            var pickups = await mongo.PickupReservations.Find(pickupFilter).SortBy(p => p.Id).Limit(50).ToListAsync(token);
            foreach (var p in pickups)
            {
                var activeUser = await mongo.Users.Find(u => u.Id == p.UserId && u.IsActive).AnyAsync(token);
                var activeBook = await mongo.Books.Find(b => b.Id == p.BookId && b.IsActive).AnyAsync(token);
                if (!activeUser || !activeBook)
                    await circulationStore.CancelPickupReservationAsync(p.Id, p.UserId, true, activeUser ? "Book discontinued" : "User account deactivated", p.Version, token);
            }
            var waitCursor = owned.GetValue("WaitCursor", "").AsString;
            var waitFilter = Builders<WaitlistEntry>.Filter.In(w => w.Status, new[] { "queued", "offered" });
            if (waitCursor.Length > 0) waitFilter &= Builders<WaitlistEntry>.Filter.Gt(w => w.Id, waitCursor);
            var waiting = await mongo.WaitlistEntries.Find(waitFilter).SortBy(w => w.Id).Limit(50).ToListAsync(token);
            foreach (var w in waiting)
            {
                var activeUser = await mongo.Users.Find(u => u.Id == w.UserId && u.IsActive).AnyAsync(token);
                var activeBook = await mongo.Books.Find(b => b.Id == w.BookId && b.IsActive).AnyAsync(token);
                if (!activeUser || !activeBook)
                    await circulationStore.LeaveWaitlistAsync(w.UserId, w.Id, activeUser ? "Book discontinued" : "User account deactivated", token);
            }
            await state.UpdateOneAsync(new BsonDocument { { "_id", "expirations" }, { "LeaseId", lease } },
                new BsonDocument("$set", new BsonDocument { { "PickupCursor", pickups.Count == 50 ? pickups[^1].Id : "" }, { "WaitCursor", waiting.Count == 50 ? waiting[^1].Id : "" }, { "LastCompletedAt", now } }), cancellationToken: token);

        }
        finally
        {
            CirculationTelemetry.Duration.Record(Math.Max(0, (clock.GetUtcNow().UtcDateTime - now).TotalSeconds));
            await state.UpdateOneAsync(
                new BsonDocument { { "_id", "expirations" }, { "LeaseId", lease } },
                new BsonDocument("$set", new BsonDocument("LeaseUntil", now)),
                cancellationToken: CancellationToken.None);
        }
    }
}

public sealed class CirculationWorker(
    IServiceScopeFactory scopes,
    IOptions<CirculationOptions> options,
    ILogger<CirculationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CirculationReminders>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                CirculationTelemetry.Failures.Add(1);
                logger.LogWarning(ex, "Circulation expiration pass unavailable; retry scheduled.");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken);
        }
    }
}
