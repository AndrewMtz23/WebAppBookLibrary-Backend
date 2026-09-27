using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class AccountRecoveryService(MongoDBService mongo, IDataProtectionProvider protection, IOptions<AccountRecoveryOptions> options, TimeProvider clock)
{
    internal const string ProtectorPurpose = "BookLibrary.AccountMail.v1";
    private readonly IDataProtector protector = protection.CreateProtector(ProtectorPurpose);
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public Task EnsureIndexesAsync(CancellationToken ct) => CreateIndexesAsync(mongo._database, ct);
    public static async Task CreateIndexesAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        await db.GetCollection<AccountChallenge>("AccountChallenges").Indexes.CreateManyAsync([
            new(Builders<AccountChallenge>.IndexKeys.Ascending(c => c.TokenHash), new CreateIndexOptions { Unique = true, Name = "ux_challenge_hash" }),
            new(Builders<AccountChallenge>.IndexKeys.Ascending(c => c.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_challenges" })
        ], cancellationToken: ct);
        await db.GetCollection<AccountMailJob>("AccountMailOutbox").Indexes.CreateManyAsync([
            new(Builders<AccountMailJob>.IndexKeys.Ascending(c => c.Stage).Ascending(c => c.AvailableAt), new CreateIndexOptions { Name = "ix_mail_pending" }),
            new(Builders<AccountMailJob>.IndexKeys.Ascending(c => c.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_mail" })
        ], cancellationToken: ct);
        await db.GetCollection<BsonDocument>("AccountRequestLimits").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAt"), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
    }

    public Task<bool> RequestResetAsync(string email, CancellationToken ct) => EnqueueAsync(new("reset", email.Trim().ToUpperInvariant()), ct);
    public Task<bool> RequestVerificationAsync(string userId, CancellationToken ct) => EnqueueAsync(new("verify", "", userId), ct);
    private async Task<bool> EnqueueAsync(AccountMailPayload payload, CancellationToken ct)
    {
        if (!options.Value.Enabled) return false;
        var now = clock.GetUtcNow().UtcDateTime;
        var limits = mongo._database.GetCollection<BsonDocument>("AccountRequestLimits");
        var id = Hash(payload.Purpose + ":" + (payload.Purpose == "reset" ? payload.Email : payload.UserId));
        var f = Builders<BsonDocument>.Filter;
        using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
        try
        {
            return await session.WithTransactionAsync(async (tx, token) =>
            {
                // The same durable path is used for known and unknown email addresses.
                var limit = await limits.Find(tx, f.Eq("_id", id)).FirstOrDefaultAsync(token);
                if (limit is not null && limit["NextAt"].ToUniversalTime() > now) return payload.Purpose == "reset";
                await limits.ReplaceOneAsync(tx, f.Eq("_id", id), new BsonDocument { ["_id"] = id, ["NextAt"] = now.AddMinutes(1), ["ExpiresAt"] = now.AddHours(2) }, new ReplaceOptions { IsUpsert = true }, token);
                var job = new AccountMailJob { ProtectedPayload = protector.Protect(JsonSerializer.Serialize(payload)), CreatedAt = now, AvailableAt = now, ExpiresAt = now.AddDays(2) };
                await mongo._database.GetCollection<AccountMailJob>("AccountMailOutbox").InsertOneAsync(tx, job, cancellationToken: token);
                return true;
            }, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return payload.Purpose == "reset"; }
        catch (MongoCommandException ex) when (ex.Code == 11000) { return payload.Purpose == "reset"; }
    }

    public async Task<bool> ConfirmAsync(string purpose, string tokenValue, string? newPassword, CancellationToken ct)
    {
        if (!options.Value.Enabled || purpose is not ("reset" or "verify") || string.IsNullOrEmpty(tokenValue) || tokenValue.Length != 43) return false;
        if (purpose == "reset" && (newPassword?.Length > 1024 || !PasswordValidator.IsValid(newPassword))) return false;
        var now = clock.GetUtcNow().UtcDateTime; var tokenHash = Hash(tokenValue);
        var challenges = mongo._database.GetCollection<AccountChallenge>("AccountChallenges");
        var newHash = purpose == "reset" ? PasswordHasher.HashPassword(newPassword!) : null;
        using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, cancellation) =>
        {
            var challenge = await challenges.Find(tx, c => c.TokenHash == tokenHash && c.Purpose == purpose && c.ConsumedAt == null && c.ExpiresAt > now).FirstOrDefaultAsync(cancellation);
            if (challenge is null) return false;
            var user = await mongo.Users.Find(tx, u => u.Id == challenge.UserId && u.IsActive).FirstOrDefaultAsync(cancellation);
            if (user is null || user.Email != challenge.Email || user.EmailVersion != challenge.EmailVersion || user.CredentialVersion != challenge.CredentialVersion || user.CredentialVersion == long.MaxValue) return false;
            var update = Builders<User>.Update.Set(u => u.UpdatedAt, now).Inc(u => u.ReferenceVersion, 1);
            update = purpose == "reset" ? update.Set(u => u.PasswordHash, newHash!).Inc(u => u.CredentialVersion, 1) : update.Set(u => u.EmailVerifiedAt, now);
            await mongo.Users.UpdateOneAsync(tx, u => u.Id == user.Id, update, cancellationToken: cancellation);
            var used = await challenges.UpdateOneAsync(tx, c => c.Id == challenge.Id && c.TokenHash == tokenHash && c.ConsumedAt == null,
                Builders<AccountChallenge>.Update.Set(c => c.ConsumedAt, now), cancellationToken: cancellation);
            if (used.ModifiedCount != 1) throw new InvalidOperationException("Challenge consumption lost its transaction guard.");
            await mongo.LogEntries.InsertOneAsync(tx, AuditLogEntryFactory.AuthenticationObserved(purpose == "reset" ? "credentials_recovered" : "email_verified", user.Id, new Dictionary<string,string> { ["result"] = "success" }, null), cancellationToken: cancellation);
            return true;
        }, cancellationToken: ct);
    }
}
