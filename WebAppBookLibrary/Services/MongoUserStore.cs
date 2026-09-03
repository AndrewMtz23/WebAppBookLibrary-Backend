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
        User? user = await _users.Find(user => user.Username == username).FirstOrDefaultAsync();
        return user;
    }

    public async Task<User?> FindByUsernameOrEmailAsync(string username, string email)
    {
        User? user = await _users.Find(user => user.Username == username || user.Email == email)
            .FirstOrDefaultAsync();
        return user;
    }

    public Task InsertAsync(User user)
    {
        return _users.InsertOneAsync(user);
    }
}
