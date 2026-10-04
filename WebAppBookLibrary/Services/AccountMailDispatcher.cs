using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public interface IAccountMailTransport
{
    Task DeliverAsync(string id, AccountMailPayload message, CancellationToken ct);
}

public sealed class AccountMailDispatcher(MongoDBService mongo, IDataProtectionProvider protection,
    IOptions<AccountRecoveryOptions> options, TimeProvider clock, IAccountMailTransport transport, IOptions<NotificationOptions>? notificationOptions = null)
{
    private readonly IDataProtector protector = protection.CreateProtector(AccountRecoveryService.ProtectorPurpose);

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return false;
        var now = clock.GetUtcNow().UtcDateTime;
        var jobs = mongo._database.GetCollection<AccountMailJob>("AccountMailOutbox");
        var notificationsEnabled = notificationOptions?.Value.Enabled ?? true;
        await jobs.UpdateManyAsync(j => (notificationsEnabled || j.NotificationId == null) && (j.Stage == "request" || j.Stage == "delivery") && j.Attempts >= 5 && j.LeaseUntil <= now,
            Builders<AccountMailJob>.Update.Set(j => j.Stage, "dead").Set(j => j.ProtectedPayload, ""), cancellationToken: ct);
        var lease = Guid.NewGuid().ToString("N");
        var job = await jobs.FindOneAndUpdateAsync(j => (notificationsEnabled || j.NotificationId == null) && (j.Stage == "request" || j.Stage == "delivery") && j.Attempts < 5 && j.AvailableAt <= now && j.LeaseUntil <= now && j.ExpiresAt > now,
            Builders<AccountMailJob>.Update.Set(j => j.LeaseId, lease).Set(j => j.LeaseUntil, now.AddMinutes(1)).Inc(j => j.Attempts, 1),
            new FindOneAndUpdateOptions<AccountMailJob> { ReturnDocument = ReturnDocument.After, Sort = Builders<AccountMailJob>.Sort.Ascending(j => j.CreatedAt) }, ct);
        if (job is null) return false;
        try
        {
            if (job.NotificationId is not null)
            {
                var message = await NotificationMail.PrepareAsync(mongo, job.NotificationId, clock.GetUtcNow().UtcDateTime, options.Value.PublicBaseUrl, notificationOptions?.Value ?? new(), ct);
                if (message is not null)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    await transport.DeliverAsync(job.Id, message, deadline.Token);
                    NotificationMetrics.Delivery("accepted");
                }
                else NotificationMetrics.Delivery("suppressed");
                await FinishAsync(); return true;
            }
            var payload = JsonSerializer.Deserialize<AccountMailPayload>(protector.Unprotect(job.ProtectedPayload))!;
            if (job.Stage == "request")
            {
                var candidate = payload.Purpose == "reset" ? await new MongoUserStore(mongo).FindByEmailAsync(payload.Email)
                    : await mongo.Users.Find(u => u.Id == payload.UserId).FirstOrDefaultAsync(ct);
                if (candidate is null) { await FinishAsync(); return true; }
                using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
                payload = await session.WithTransactionAsync(async (tx, cancellation) =>
                {
                    var owned = await jobs.Find(tx, j => j.Id == job.Id && j.LeaseId == lease && j.Stage == "request").FirstOrDefaultAsync(cancellation);
                    if (owned is null) return null;
                    var user = await mongo.Users.Find(tx, u => u.Id == candidate.Id && u.IsActive).FirstOrDefaultAsync(cancellation);
                    if (user is null || (payload.Purpose == "verify" && user.EmailVerifiedAt != null) ||
                        (payload.Purpose == "reset" && user.Email.Trim().ToUpperInvariant() != payload.Email)) return null;
                    var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                    var challenge = new AccountChallenge { Id = user.Id + ":" + payload.Purpose, UserId = user.Id, Purpose = payload.Purpose,
                        Email = user.Email, EmailVersion = user.EmailVersion, CredentialVersion = user.CredentialVersion,
                        TokenHash = AccountRecoveryService.Hash(token), CreatedAt = now,
                        ExpiresAt = now.AddMinutes(payload.Purpose == "reset" ? options.Value.ResetMinutes : options.Value.VerificationMinutes) };
                    await mongo.Users.UpdateOneAsync(tx, u => u.Id == user.Id, Builders<User>.Update.Inc(u => u.ReferenceVersion, 1), cancellationToken: cancellation);
                    await mongo._database.GetCollection<AccountChallenge>("AccountChallenges").ReplaceOneAsync(tx, c => c.Id == challenge.Id, challenge, new ReplaceOptions { IsUpsert = true }, cancellation);
                    var route = payload.Purpose == "reset" ? "reset-password" : "verify-email";
                    var ready = new AccountMailPayload(payload.Purpose, user.Email, user.Id, token, options.Value.PublicBaseUrl.TrimEnd('/') + "/auth/" + route + "#token=" + token);
                    await jobs.UpdateOneAsync(tx, j => j.Id == job.Id && j.LeaseId == lease,
                        Builders<AccountMailJob>.Update.Set(j => j.Stage, "delivery").Set(j => j.ProtectedPayload, protector.Protect(JsonSerializer.Serialize(ready))), cancellationToken: cancellation);
                    return ready;
                }, cancellationToken: ct);
                if (payload is null) { await FinishAsync(); return true; }
            }
            var hash = AccountRecoveryService.Hash(payload.Token);
            var current = await mongo._database.GetCollection<AccountChallenge>("AccountChallenges")
                .Find(c => c.TokenHash == hash && c.ConsumedAt == null && c.ExpiresAt > now).FirstOrDefaultAsync(ct);
            var account = await mongo.Users.Find(u => u.Id == payload.UserId && u.IsActive).FirstOrDefaultAsync(ct);
            if (current != null && account != null && current.Email == account.Email && current.EmailVersion == account.EmailVersion && current.CredentialVersion == account.CredentialVersion)
                await transport.DeliverAsync(job.Id, payload, ct);
            await FinishAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (job.NotificationId is not null) NotificationMetrics.Delivery(job.Attempts >= 5 ? "dead" : "retry");
            // Never log encrypted messages, addresses, tokens or provider exception bodies.
            var update = Builders<AccountMailJob>.Update.Set(j => j.LeaseUntil, now).Set(j => j.AvailableAt, now.AddSeconds(Math.Pow(2, job.Attempts) * 10));
            if (job.Attempts >= 5) update = update.Set(j => j.Stage, "dead").Set(j => j.ProtectedPayload, "");
            await jobs.UpdateOneAsync(j => j.Id == job.Id && j.LeaseId == lease, update, cancellationToken: ct);
        }
        return true;

        Task FinishAsync() => jobs.UpdateOneAsync(j => j.Id == job.Id && j.LeaseId == lease,
            Builders<AccountMailJob>.Update.Set(j => j.Stage, "done").Set(j => j.ProtectedPayload, "").Set(j => j.LeaseUntil, now), cancellationToken: ct);
    }
}

// Explicit development transport: writes only to the configured private local mailbox.
public sealed class LocalAccountMailTransport(IOptions<AccountRecoveryOptions> options) : IAccountMailTransport
{
    public async Task DeliverAsync(string id, AccountMailPayload message, CancellationToken ct)
    {
        var directory = options.Value.LocalMailDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("Local mailbox is not configured.");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, id + ".json");
        if (File.Exists(destination)) return;
        var temporary = Path.Combine(directory, id + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(message), ct);
            try { File.Move(temporary, destination, false); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class AccountMailWorker(IServiceScopeFactory scopes, ILogger<AccountMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<AccountMailDispatcher>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Account mail processing unavailable; retry scheduled."); }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
