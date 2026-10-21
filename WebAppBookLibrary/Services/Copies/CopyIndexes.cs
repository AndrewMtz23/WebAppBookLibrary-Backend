using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Copies;

public static class CopyIndexes
{
    public static async Task CreateAsync(IMongoDatabase db)
    {
        var keys=Builders<BookCopy>.IndexKeys;
        await db.GetCollection<BookCopy>("BookCopies").Indexes.CreateManyAsync([
            new(keys.Ascending(c=>c.InventoryCode),new CreateIndexOptions { Name="ux_copy_code",Unique=true }),
            new(keys.Ascending(c=>c.CreationKey),new CreateIndexOptions<BookCopy> { Name="ux_copy_creation",Unique=true,PartialFilterExpression=Builders<BookCopy>.Filter.Exists(c=>c.CreationKey) }),
            new(keys.Ascending(c=>c.ActiveAssignmentType).Ascending(c=>c.ActiveAssignmentId),new CreateIndexOptions<BookCopy> { Name="ux_copy_assignment",Unique=true,PartialFilterExpression=Builders<BookCopy>.Filter.Exists(c=>c.ActiveAssignmentId) }),
            new(keys.Ascending(c=>c.BookId).Ascending(c=>c.Status).Ascending(c=>c.InventoryCode),new CreateIndexOptions { Name="ix_copy_book_status" })
        ]);
        await db.GetCollection<CopyHistoryEntry>("CopyHistory").Indexes.CreateOneAsync(new CreateIndexModel<CopyHistoryEntry>(
            Builders<CopyHistoryEntry>.IndexKeys.Ascending(h=>h.CopyId).Descending(h=>h.CreatedAt),new CreateIndexOptions{Name="ix_copy_history"}));
        await db.GetCollection<CopyHistoryEntry>("CopyHistory").Indexes.CreateOneAsync(new CreateIndexModel<CopyHistoryEntry>(
            Builders<CopyHistoryEntry>.IndexKeys.Ascending(h=>h.OperationKey),new CreateIndexOptions<CopyHistoryEntry>{Name="ux_copy_operation",Unique=true,PartialFilterExpression=Builders<CopyHistoryEntry>.Filter.Exists(h=>h.OperationKey)}));
    }
}
