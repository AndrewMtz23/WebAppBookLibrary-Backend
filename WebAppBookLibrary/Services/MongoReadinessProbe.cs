using MongoDB.Bson;
using MongoDB.Driver;

namespace WebAppBookLibrary.Services;

public interface IReadinessProbe
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

public sealed class MongoReadinessProbe(MongoDBService database) : IReadinessProbe
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await database._database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (MongoException) { return false; }
        catch (TimeoutException) { return false; }
    }
}
