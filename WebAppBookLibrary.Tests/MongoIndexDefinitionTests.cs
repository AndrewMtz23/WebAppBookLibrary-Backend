using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class MongoIndexDefinitionTests
{
    [Fact]
    public void Partial_index_filters_use_atlas_supported_range_operators()
    {
        AssertSupported(MongoDBService.NormalizedUsernameIndexFilter(), BsonSerializer.LookupSerializer<User>());
        AssertSupported(MongoDBService.NormalizedEmailIndexFilter(), BsonSerializer.LookupSerializer<User>());
        AssertSupported(MongoDBService.IsbnIndexFilter(), BsonSerializer.LookupSerializer<Book>());
        AssertSupported(MongoDBService.ActiveReservationIndexFilter(), BsonSerializer.LookupSerializer<Loan>());
    }

    private static void AssertSupported<T>(FilterDefinition<T> filter, IBsonSerializer<T> serializer)
    {
        var rendered = filter.Render(new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry)).ToJson();
        Assert.Contains("$gt", rendered);
        Assert.DoesNotContain("$ne", rendered);
        Assert.DoesNotContain("$not", rendered);
    }
}
