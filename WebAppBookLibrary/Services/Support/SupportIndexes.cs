using MongoDB.Driver;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Support;

public static class SupportIndexes
{
    public static async Task CreateAsync(IMongoDatabase db)
    {
        await db.GetCollection<SupportTicket>("SupportTickets").Indexes.CreateManyAsync([
            new(Builders<SupportTicket>.IndexKeys.Ascending(t => t.RequesterId).Ascending(t => t.IdempotencyKey), new CreateIndexOptions { Unique = true }),
            new(Builders<SupportTicket>.IndexKeys.Ascending(t => t.RequesterId).Descending(t => t.CreatedAt).Descending(t => t.Id)),
            new(Builders<SupportTicket>.IndexKeys.Ascending(t => t.Category).Ascending(t => t.Status).Descending(t => t.CreatedAt).Descending(t => t.Id)),
            new(Builders<SupportTicket>.IndexKeys.Ascending(t => t.Status).Ascending(t => t.ClosedAt))]);
        await db.GetCollection<SupportReply>("SupportReplies").Indexes.CreateOneAsync(new CreateIndexModel<SupportReply>(Builders<SupportReply>.IndexKeys.Ascending(t => t.TicketId).Ascending(t => t.CreatedAt).Ascending(t => t.Id)));
        await db.GetCollection<SupportReply>("SupportReplies").Indexes.CreateOneAsync(new CreateIndexModel<SupportReply>(Builders<SupportReply>.IndexKeys.Ascending(t => t.AuthorId).Ascending(t => t.CreatedAt)));
        await db.GetCollection<SupportInternalNote>("SupportInternalNotes").Indexes.CreateOneAsync(new CreateIndexModel<SupportInternalNote>(Builders<SupportInternalNote>.IndexKeys.Ascending(t => t.TicketId).Ascending(t => t.CreatedAt).Ascending(t => t.Id)));
        await db.GetCollection<SupportInternalNote>("SupportInternalNotes").Indexes.CreateOneAsync(new CreateIndexModel<SupportInternalNote>(Builders<SupportInternalNote>.IndexKeys.Ascending(t => t.AuthorId).Ascending(t => t.CreatedAt)));
        await db.GetCollection<SupportHistory>("SupportHistory").Indexes.CreateOneAsync(new CreateIndexModel<SupportHistory>(Builders<SupportHistory>.IndexKeys.Ascending(t => t.TicketId).Ascending(t => t.CreatedAt).Ascending(t => t.Id)));
    }
}
