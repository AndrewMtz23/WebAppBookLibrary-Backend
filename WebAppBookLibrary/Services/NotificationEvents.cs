using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

/// <summary>Called only inside the business transaction. No transport I/O here.</summary>
public static class NotificationEvents
{
    public static async Task AppendAsync(IMongoDatabase db, IClientSessionHandle tx, Loan loan, string type, DateTime now, CancellationToken ct)
    {
        var key = loan.Id + ":" + type + (type.StartsWith("due_") ? ":" + loan.DueAt?.Ticks : "");
        var title = type switch { "reserved" => "Reserva confirmada", "returned" => "Devolución confirmada", "cancelled" => "Reserva cancelada", "due_overdue" => "Tu préstamo ha vencido", _ => "Tu préstamo vence pronto" };
        var body = type switch { "reserved" => "Tu libro ya está reservado.", "returned" => "Registramos la devolución de tu libro.", "cancelled" => "Tu reserva se canceló correctamente.", _ => "Revisa la fecha de devolución de tu préstamo físico." };
        await AppendEventAsync(db, tx, loan.UserId, key, type, title, body, loan.BookId, loan.Id, loan.DueAt, now, ct);
    }

    public static async Task AppendEventAsync(
        IMongoDatabase db,
        IClientSessionHandle tx,
        string userId,
        string eventKey,
        string type,
        string title,
        string body,
        string? targetId,
        string? loanId,
        DateTime? dueAt,
        DateTime now,
        CancellationToken ct)
    {
        var notices = db.GetCollection<Notification>("Notifications");
        if (await notices.Find(tx, n => n.UserId == userId && n.EventKey == eventKey).AnyAsync(ct)) return;
        var preferences = db.GetCollection<NotificationPreferences>("NotificationPreferences");
        var prefs = await preferences.FindOneAndUpdateAsync<NotificationPreferences>(tx, p => p.Id == userId,
            Builders<NotificationPreferences>.Update.Inc(p => p.Sequence, 1).SetOnInsert(p => p.Reminders, true).SetOnInsert(p => p.Email, false),
            new FindOneAndUpdateOptions<NotificationPreferences> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
        if (type.StartsWith("due_") && !prefs.Reminders) return;
        var notice = new Notification { UserId = userId, Sequence = prefs.Sequence, EventKey = eventKey, Type = type, LoanId = loanId ?? "",
            TargetId = targetId ?? "", DueAt = dueAt, CreatedAt = now,
            Title = title, Body = body };
        await notices.InsertOneAsync(tx, notice, cancellationToken: ct);
        if (prefs.Email)
            await db.GetCollection<AccountMailJob>("AccountMailOutbox").InsertOneAsync(tx,
                new AccountMailJob { Id = "notification-" + notice.Id, NotificationId = notice.Id, Stage = "delivery", CreatedAt = now,
                    AvailableAt = now, ExpiresAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) }, cancellationToken: ct);
    }

    public static Task CreateIndexesAsync(IMongoDatabase db) => db.GetCollection<Notification>("Notifications").Indexes.CreateManyAsync([
        new(Builders<Notification>.IndexKeys.Ascending(n => n.UserId).Ascending(n => n.EventKey), new CreateIndexOptions { Unique = true, Name = "ux_notification_event" }),
        new(Builders<Notification>.IndexKeys.Ascending(n => n.UserId).Descending(n => n.Sequence), new CreateIndexOptions { Unique = true, Name = "ux_notification_sequence" }),
        new(Builders<Notification>.IndexKeys.Ascending(n => n.UserId).Ascending(n => n.ReadAt).Descending(n => n.Sequence), new CreateIndexOptions { Name = "ix_notification_unread" })
    ]);
}
