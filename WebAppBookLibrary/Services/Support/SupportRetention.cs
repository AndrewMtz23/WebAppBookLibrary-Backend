using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Support;

public sealed class SupportRetention(MongoDBService mongo, IOptions<SupportOptions> options)
{
    public async Task<int> PurgeExpiredAsync(DateTime now, int batchSize, CancellationToken ct)
    {
        var tickets = mongo._database.GetCollection<SupportTicket>("SupportTickets");
        var candidateBefore = now.AddYears(-options.Value.RetentionYears).AddDays(1);
        var candidates = await tickets.Find(t => t.Status == "closed" && t.ClosedAt <= candidateBefore).SortBy(t => t.ClosedAt).Limit(Math.Clamp(batchSize, 1, 100)).ToListAsync(ct);
        var count = 0;
        foreach (var candidate in candidates)
        {
            using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
            count += await session.WithTransactionAsync(async (tx, token) => {
                var ticket = await tickets.Find(tx, t => t.Id == candidate.Id).FirstOrDefaultAsync(token);
                if (ticket?.Status != "closed" || !ticket.ClosedAt.HasValue || ticket.ClosedAt.Value.AddYears(options.Value.RetentionYears) > now) return 0;
                await DeleteTicketAsync(mongo._database, tx, ticket.Id, token);
                await RecordAsync(mongo._database, tx, "retention", 1, now, token);
                return 1;
            }, cancellationToken: ct);
        }
        await mongo._database.GetCollection<BsonDocument>("SupportPurgeEvents").DeleteManyAsync(Builders<BsonDocument>.Filter.Lte("CreatedAt", now.AddDays(-180)), ct);
        return count;
    }

    public static async Task DeleteOwnedAsync(IMongoDatabase db, IClientSessionHandle tx, string userId, CancellationToken ct)
    {
        var tickets = await db.GetCollection<SupportTicket>("SupportTickets").Find(tx, t => t.RequesterId == userId).Project(t => t.Id).ToListAsync(ct);
        foreach (var id in tickets) await DeleteTicketAsync(db, tx, id, ct);
        if (tickets.Count > 0) await RecordAsync(db, tx, "account_deletion", tickets.Count, DateTime.UtcNow, ct);
    }
    public static async Task AnonymizeStaffAsync(IMongoDatabase db, IClientSessionHandle tx, string userId, CancellationToken ct)
    {
        await db.GetCollection<SupportReply>("SupportReplies").UpdateManyAsync(tx, r => r.AuthorId == userId, Builders<SupportReply>.Update.Set(r => r.AuthorId, null), cancellationToken: ct);
        await db.GetCollection<SupportInternalNote>("SupportInternalNotes").UpdateManyAsync(tx, r => r.AuthorId == userId, Builders<SupportInternalNote>.Update.Set(r => r.AuthorId, null), cancellationToken: ct);
        await db.GetCollection<SupportHistory>("SupportHistory").UpdateManyAsync(tx, r => r.ActorId == userId, Builders<SupportHistory>.Update.Set(r => r.ActorId, null), cancellationToken: ct);
        await db.GetCollection<SupportTicket>("SupportTickets").UpdateManyAsync(tx, r => r.AssigneeId == userId, Builders<SupportTicket>.Update.Set(r => r.AssigneeId, null).Inc(r => r.Version, 1), cancellationToken: ct);
    }
    private static async Task DeleteTicketAsync(IMongoDatabase db, IClientSessionHandle tx, string id, CancellationToken ct)
    {
        await db.GetCollection<SupportTicket>("SupportTickets").DeleteOneAsync(tx, t => t.Id == id, cancellationToken: ct);
        await db.GetCollection<SupportReply>("SupportReplies").DeleteManyAsync(tx, r => r.TicketId == id, cancellationToken: ct);
        await db.GetCollection<SupportInternalNote>("SupportInternalNotes").DeleteManyAsync(tx, r => r.TicketId == id, cancellationToken: ct);
        await db.GetCollection<SupportHistory>("SupportHistory").DeleteManyAsync(tx, r => r.TicketId == id, cancellationToken: ct);
        var notices = db.GetCollection<Notification>("Notifications");
        var ids = await notices.Find(tx, n => n.TargetType == "support_ticket" && n.TargetId == id).Project(n => n.Id).ToListAsync(ct);
        await db.GetCollection<AccountMailJob>("AccountMailOutbox").DeleteManyAsync(tx, Builders<AccountMailJob>.Filter.In(j => j.NotificationId, ids), cancellationToken: ct);
        await notices.DeleteManyAsync(tx, n => n.TargetType == "support_ticket" && n.TargetId == id, cancellationToken: ct);
    }
    private static Task RecordAsync(IMongoDatabase db, IClientSessionHandle tx, string type, int count, DateTime now, CancellationToken ct) =>
        db.GetCollection<BsonDocument>("SupportPurgeEvents").InsertOneAsync(tx, new() { { "Type", type }, { "Count", count }, { "CreatedAt", now } }, cancellationToken: ct);
}
