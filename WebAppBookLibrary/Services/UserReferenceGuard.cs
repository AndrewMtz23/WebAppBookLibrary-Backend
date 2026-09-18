using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

internal static class UserReferenceGuard
{
    public static async Task TouchAsync(IMongoCollection<User> users, IClientSessionHandle transaction, string userId, CancellationToken token)
    {
        // A real write serializes new references with account deactivation and deletion.
        var changed = await users.UpdateOneAsync(transaction, user => user.Id == userId && user.IsActive,
            Builders<User>.Update.Inc(user => user.ReferenceVersion, 1), cancellationToken: token);
        if (changed.MatchedCount != 1) throw new UserReferenceUnavailableException();
    }
}

public sealed class UserReferenceUnavailableException : InvalidOperationException;
