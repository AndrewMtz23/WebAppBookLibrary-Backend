using MongoDB.Driver;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Support;

public static class SupportNotifications
{
    public static Task AppendAsync(IMongoDatabase db, IClientSessionHandle tx, SupportTicket ticket, string eventKey, DateTime now, CancellationToken ct) =>
        NotificationEvents.AppendEventAsync(db, tx, ticket.RequesterId, eventKey, "support_update", "Actualización de soporte",
            "Hay una actualización en tu solicitud. Inicia sesión para consultarla.", ticket.Id, null, null, now, ct, "support_ticket");
}
