using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services
{
    public class UserService
    {
        private readonly IMongoCollection<User> _users;

        public UserService(MongoDBService mongoDBService)
        {
            _users = mongoDBService.Users;
        }

        public async Task<User?> GetUserByUserNameAsync(string username)
        {
            return await _users.Find(u => u.Username == username && u.IsActive)
                .FirstOrDefaultAsync();
        }

        public async Task<User?> CreateUserAsync(User user, string password)
        {
            var existingUser = await _users.Find(u => u.Username == user.Username)
                .FirstOrDefaultAsync();
            if (existingUser != null)
            {
                return null;
            }

            user.PasswordHash = PasswordHasher.HashPassword(password);
            await _users.InsertOneAsync(user);
            return user;
        }

        public async Task<bool> UpdateUserAsync(string userId, User updatedUser, string? newPassword = null)
        {
            var update = Builders<User>.Update
                .Set(u => u.Email, updatedUser.Email);

            if (!string.IsNullOrEmpty(newPassword))
            {
                update = update.Set(u => u.PasswordHash, PasswordHasher.HashPassword(newPassword));
            }

            var result = await _users.UpdateOneAsync(
                u => u.Id == userId,
                update);

            return result.ModifiedCount > 0;
        }

        public async Task<bool> DeActivateUserAsync(string userId)
        {
            var update = Builders<User>.Update
                .Set(u => u.IsActive, false);

            var result = await _users.UpdateOneAsync(
                u => u.Id == userId,
                update);

            return result.ModifiedCount > 0;
        }
    }
}