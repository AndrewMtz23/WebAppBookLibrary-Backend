using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Copies;

public static class CopyRetention
{
    public static async Task AnonymizeActorAsync(IMongoDatabase db,IClientSessionHandle tx,string userId,CancellationToken ct)
    {
        await db.GetCollection<CopyHistoryEntry>("CopyHistory").UpdateManyAsync(tx,
            h=>h.ActorId==userId,
            Builders<CopyHistoryEntry>.Update.Set(h=>h.ActorId,null)
                .Unset(h=>h.OperationKey).Unset(h=>h.OperationFingerprint),cancellationToken:ct);
        var prefix=userId+":";
        await db.GetCollection<BookCopy>("BookCopies").UpdateManyAsync(tx,
            c=>c.CreationKey!=null&&c.CreationKey.StartsWith(prefix),
            Builders<BookCopy>.Update.Unset(c=>c.CreationKey).Unset(c=>c.CreationFingerprint),cancellationToken:ct);
    }
}
