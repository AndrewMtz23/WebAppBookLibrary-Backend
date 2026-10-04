using MongoDB.Bson;
using MongoDB.Driver;

namespace WebAppBookLibrary.Services;

/// <summary>One persisted mode shared by all compatible application instances.</summary>
public static class CirculationMode
{
    public static async Task<string> ReadAsync(IMongoDatabase db, string initialMode, CancellationToken ct)
    {
        var states = db.GetCollection<BsonDocument>("CirculationState");
        await states.UpdateOneAsync(new BsonDocument("_id", "policy"), new BsonDocument("$setOnInsert", new BsonDocument { { "Mode", initialMode }, { "Version", 0L } }), new UpdateOptions { IsUpsert = true }, ct);
        return (await states.Find(new BsonDocument("_id", "policy")).SingleAsync(ct))["Mode"].AsString;
    }
    public static async Task<string> GuardAsync(IMongoDatabase db, IClientSessionHandle tx, CancellationToken ct)
    {
        var document = await db.GetCollection<BsonDocument>("CirculationState").FindOneAndUpdateAsync(tx,
            new BsonDocument("_id", "policy"), new BsonDocument("$inc", new BsonDocument("Version", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        return document?["Mode"].AsString ?? "legacy";
    }
}
