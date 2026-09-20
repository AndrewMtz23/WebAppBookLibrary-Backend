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

    public async Task<User?> UpdateProfileAsync(string userId, WebAppBookLibrary.Contracts.Profile.UpdateProfileRequest request, CancellationToken token)
    {
        var at = new MongoDB.Bson.BsonDateTime(DateTime.UtcNow).ToUniversalTime();
        if (at <= request.ExpectedUpdatedAt) at = request.ExpectedUpdatedAt.AddMilliseconds(1);
        var update = Builders<User>.Update.Set(u => u.DisplayName, request.DisplayName)
            .Set(u => u.Email, request.Email).Set(u => u.NormalizedEmail, request.Email.ToUpperInvariant())
            .Set(u => u.AvatarUrl, request.AvatarUrl).Set(u => u.UpdatedAt, at);
        try
        {
            return await _users.FindOneAndUpdateAsync(
                u => u.Id == userId && u.IsActive && u.UpdatedAt == request.ExpectedUpdatedAt,
                update, new FindOneAndUpdateOptions<User> { ReturnDocument = ReturnDocument.After }, token);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return null; }
        catch (MongoCommandException ex) when (ex.Code == 11000) { return null; }
    }

    public Task TouchLastLoginAsync(string userId, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        var update = Builders<User>.Update
            .Set(user => user.LastLoginAt, occurredAtUtc)
            .Set(user => user.UpdatedAt, occurredAtUtc);
        return _users.UpdateOneAsync(user => user.Id == userId, update, cancellationToken: cancellationToken);
    }
}
