using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed record CirculationInventory(string BookId, int Total, int Available, int Retained, int Loaned, bool Consistent);
public sealed record CirculationReconciliation(IReadOnlyList<CirculationInventory> Books, int LegacyLoans, bool Applied);

public static class CirculationMaintenance
{
    // Called by the operator on an isolated copy first. Changes and mode switch share a transaction.
    public static async Task<CirculationReconciliation> RunAsync(MongoDBService mongo, string mode, bool apply, CancellationToken ct)
    {
        if (mode is not ("legacy" or "active" or "draining")) throw new ArgumentException("Invalid mode");
        if (apply) await CirculationMode.ReadAsync(mongo._database, "legacy", ct);
        using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) => {
            if (apply) await CirculationMode.GuardAsync(mongo._database, tx, token);
            var books = await mongo.Books.Find(tx, b => b.MediaType == "physical").ToListAsync(token);
            var loans = await mongo.Loans.Find(tx, FilterDefinition<Loan>.Empty).ToListAsync(token);
            var pickups = await mongo.PickupReservations.Find(tx, p => p.Status == "ready").ToListAsync(token);
            var now = DateTime.UtcNow;
            var rows = books.Select(b => {
                int loaned = loans.Count(l => l.BookId == b.Id && LoanResponse.From(l, now).Status is "active" or "overdue");
                int retained = pickups.Count(p => p.BookId == b.Id);
                int total = b.TotalCopies ?? -1, available = b.AvailableCopies ?? -1;
                return new CirculationInventory(b.Id, total, available, retained, loaned, total >= 0 && available >= 0 && retained == (b.RetainedCopies ?? 0) && available + retained + loaned == total);
            }).ToArray();
            var legacy = loans.Count(l => string.IsNullOrWhiteSpace(l.PolicyVersion));
            if (!apply) return new CirculationReconciliation(rows, legacy, false);
            if (rows.Any(r => !r.Consistent)) throw new InvalidOperationException("Inventory reconciliation required; no changes applied.");
            if (mode == "legacy" && (pickups.Count > 0 || await mongo.WaitlistEntries.Find(tx, w => w.Status == "queued" || w.Status == "offered").AnyAsync(token) || loans.Any(l => l.PolicyVersion == "circulation-v1")))
                throw new InvalidOperationException("Use draining while circulation references exist.");
            foreach (var b in books) await mongo.Books.UpdateOneAsync(tx, item => item.Id == b.Id, Builders<Book>.Update.Inc(item => item.ReferenceVersion, 1), cancellationToken: token);
            await mongo.Loans.UpdateManyAsync(tx, l => l.PolicyVersion == null || l.PolicyVersion == "", Builders<Loan>.Update.Set(l => l.PolicyVersion, "legacy"), cancellationToken: token);
            await mongo._database.GetCollection<BsonDocument>("CirculationState").UpdateOneAsync(tx, new BsonDocument("_id", "policy"), new BsonDocument("$set", new BsonDocument("Mode", mode)), cancellationToken: token);
            return new CirculationReconciliation(rows, legacy, true);
        }, cancellationToken: ct);
    }
}
