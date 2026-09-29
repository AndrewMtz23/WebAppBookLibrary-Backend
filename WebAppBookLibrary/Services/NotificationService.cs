using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed record NotificationResponse(string Id, string Type, string Title, string Body, string TargetType, string TargetId, DateTime CreatedAt, DateTime? ReadAt, DateTime? DueAt);
public sealed record NotificationPage(IReadOnlyList<NotificationResponse> Items, long? NextCursor, long ReadThrough, long UnreadCount);
public sealed record NotificationPreferencesResponse(bool Reminders, bool Email, bool EmailVerified);
public sealed record SaveNotificationPreferences(bool Reminders, bool Email);
public sealed record ReadNotificationsRequest(long Through);

public sealed class NotificationService(MongoDBService mongo, TimeProvider clock)
{
    private IMongoCollection<Notification> Notices => mongo._database.GetCollection<Notification>("Notifications");
    private IMongoCollection<NotificationPreferences> Preferences => mongo._database.GetCollection<NotificationPreferences>("NotificationPreferences");
    public async Task<bool> IsActiveAsync(string userId, CancellationToken ct) => ObjectId.TryParse(userId, out _) && await mongo.Users.Find(u => u.Id == userId && u.IsActive).AnyAsync(ct);
    public Task<long> UnreadCountAsync(string userId, CancellationToken ct) => Notices.CountDocumentsAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken: ct);

    public async Task<NotificationPage> ListAsync(string userId, long? before, int size, bool? read, CancellationToken ct)
    {
        // Reading this counter first establishes a committed prefix, even if another producer commits during the query.
        var through = (await Preferences.Find(p => p.Id == userId).FirstOrDefaultAsync(ct))?.Sequence ?? 0;
        var f = Builders<Notification>.Filter;
        var filter = f.Eq(n => n.UserId, userId) & f.Lte(n => n.Sequence, through);
        if (before.HasValue) filter &= f.Lt(n => n.Sequence, before.Value);
        if (read.HasValue) filter &= read.Value ? f.Ne(n => n.ReadAt, null) : f.Eq(n => n.ReadAt, null);
        var rows = await Notices.Find(filter).SortByDescending(n => n.Sequence).Limit(size + 1).ToListAsync(ct);
        return new(rows.Take(size).Select(n => new NotificationResponse(n.Id, n.Type, n.Title, n.Body, n.TargetType, n.TargetId, n.CreatedAt, n.ReadAt, n.DueAt)).ToArray(),
            rows.Count > size ? rows[size - 1].Sequence : null, through, await UnreadCountAsync(userId, ct));
    }

    public async Task<bool> ReadAsync(string userId, string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _)) return false;
        await Notices.UpdateOneAsync(n => n.Id == id && n.UserId == userId && n.ReadAt == null,
            Builders<Notification>.Update.Set(n => n.ReadAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
        return await Notices.Find(n => n.Id == id && n.UserId == userId).AnyAsync(ct);
    }
    public Task ReadAllAsync(string userId, long through, CancellationToken ct) => Notices.UpdateManyAsync(
        n => n.UserId == userId && n.Sequence <= through && n.ReadAt == null,
        Builders<Notification>.Update.Set(n => n.ReadAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);

    public async Task<NotificationPreferencesResponse> GetPreferencesAsync(string userId, CancellationToken ct)
    {
        var prefs = await Preferences.Find(p => p.Id == userId).FirstOrDefaultAsync(ct);
        var user = await mongo.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        return new(prefs?.Reminders ?? true, prefs?.Email ?? false, user?.EmailVerifiedAt != null);
    }
    public async Task<bool> SavePreferencesAsync(string userId, SaveNotificationPreferences request, CancellationToken ct)
    {
        using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) => {
            var account = await mongo.Users.Find(tx, u => u.Id == userId && u.IsActive).FirstOrDefaultAsync(token);
            if (account is null || (request.Email && account.EmailVerifiedAt is null)) return false;
            await UserReferenceGuard.TouchAsync(mongo.Users, tx, userId, token);
            await Preferences.UpdateOneAsync(tx, p => p.Id == userId,
                Builders<NotificationPreferences>.Update.Set(p => p.Reminders, request.Reminders).Set(p => p.Email, request.Email),
                new UpdateOptions { IsUpsert = true }, token);
            return true;
        }, cancellationToken: ct);
    }
}
