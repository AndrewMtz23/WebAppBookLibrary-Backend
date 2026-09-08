using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class MongoUserStore : IUserStore
{
    private readonly IMongoCollection<User> _users;

    public MongoUserStore(MongoDBService mongoDBService)
    {
        _users = mongoDBService.Users;
    }

    public async Task<User?> FindByUsernameAsync(string username)
    {
        var normalized = username.Trim().ToUpperInvariant();
        User? user = await _users.Find(user => user.NormalizedUsername == normalized || user.Username == username).FirstOrDefaultAsync();
        return user;
    }

    public async Task<User?> FindByUsernameOrEmailAsync(string username, string email)
    {
        var normalizedUsername = username.Trim().ToUpperInvariant();
        var normalizedEmail = email.Trim().ToUpperInvariant();
        User? user = await _users.Find(user => user.NormalizedUsername == normalizedUsername || user.NormalizedEmail == normalizedEmail || user.Username == username || user.Email == email)
            .FirstOrDefaultAsync();
        return user;
    }

    public Task InsertAsync(User user)
    {
        return _users.InsertOneAsync(user);
    }

    public Task TouchLastLoginAsync(string userId, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        var update = Builders<User>.Update
            .Set(user => user.LastLoginAt, occurredAtUtc)
            .Set(user => user.UpdatedAt, occurredAtUtc);
        return _users.UpdateOneAsync(user => user.Id == userId, update, cancellationToken: cancellationToken);
    }
}
