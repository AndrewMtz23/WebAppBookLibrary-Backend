using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public static class NotificationMail
{
    public static async Task<AccountMailPayload?> PrepareAsync(MongoDBService mongo, string id, DateTime now, string baseUrl, NotificationOptions options, CancellationToken ct)
    {
        var notice = await mongo._database.GetCollection<Notification>("Notifications").Find(n => n.Id == id).FirstOrDefaultAsync(ct);
        if (notice is null) return null;
        var prefs = await mongo._database.GetCollection<NotificationPreferences>("NotificationPreferences").Find(p => p.Id == notice.UserId).FirstOrDefaultAsync(ct);
        var account = await mongo.Users.Find(u => u.Id == notice.UserId && u.IsActive && u.EmailVerifiedAt != null).FirstOrDefaultAsync(ct);
        if (account is null || prefs?.Email != true) return null;
        if (notice.Type == "pickup_ready")
        {
            var parts = notice.EventKey.Split(':');
            if (parts.Length < 2 || !MongoDB.Bson.ObjectId.TryParse(parts[1], out _)) return null;
            var pickup = await mongo.PickupReservations.Find(p => p.Id == parts[1] && p.Status == "ready" && p.PickupExpiresAt > now).FirstOrDefaultAsync(ct);
            if (pickup == null || !await mongo.Books.Find(b => b.Id == pickup.BookId && b.IsActive).AnyAsync(ct)) return null;
        }
        if (notice.Type.StartsWith("due_"))
        {
            if (!prefs.Reminders) return null;
            var loan = await mongo.Loans.Find(l => l.Id == notice.LoanId).FirstOrDefaultAsync(ct);
            if (loan is null || LoanResponse.From(loan, now).DueAt != notice.DueAt || NotificationReminders.CurrentType(loan, now, options) != notice.Type) return null;
        }
        return new("notification", account.Email, account.Id, Link: baseUrl.TrimEnd('/') + "/app/notifications", Title: notice.Title,
            Body: notice.Body + (notice.DueAt.HasValue ? " Vencimiento: " + notice.DueAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'") + "." : ""));
    }
}
