using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class PasswordSecurityService(MongoDBService database)
{
    public async Task<string?> ChangeAsync(string userId, long? expectedVersion, string currentPassword, string newPassword, CancellationToken ct)
    {
        if (!ObjectId.TryParse(userId, out _) || expectedVersion is null) return "session_changed";
        if (string.IsNullOrEmpty(currentPassword) || currentPassword.Length > 1024 || newPassword?.Length > 1024 || !PasswordValidator.IsValid(newPassword)) return "password_invalid";
        var user = await database.Users.Find(u => u.Id == userId && u.IsActive).FirstOrDefaultAsync(ct);
        if (user is null || user.CredentialVersion != expectedVersion || user.CredentialVersion == long.MaxValue) return "session_changed";
        if (!PasswordHasher.VerifyPassword(currentPassword, user.PasswordHash)) return "current_password_invalid";
        if (currentPassword == newPassword) return "password_unchanged";
        var hash = PasswordHasher.HashPassword(newPassword!);
        var f = Builders<User>.Filter;
        var version = f.Eq(u => u.CredentialVersion, user.CredentialVersion);
        if (user.CredentialVersion == 0) version |= f.Exists(u => u.CredentialVersion, false);
        var changed = await database.Users.UpdateOneAsync(
            f.Eq(u => u.Id, userId) & f.Eq(u => u.IsActive, true) & f.Eq(u => u.PasswordHash, user.PasswordHash) & version,
            Builders<User>.Update.Set(u => u.PasswordHash, hash).Inc(u => u.CredentialVersion, 1).Set(u => u.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
        return changed.ModifiedCount == 1 ? null : "session_changed";
    }
}
