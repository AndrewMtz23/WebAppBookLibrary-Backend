using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;

namespace WebAppBookLibrary.Services;

public sealed class MongoAdminUserStore : IAdminUserStore
{
    private readonly IMongoCollection<User> _users;
    private readonly IMongoCollection<BsonDocument> _mutationGuards;
    public MongoAdminUserStore(MongoDBService database) : this(database.Users) { }
    public MongoAdminUserStore(IMongoCollection<User> users)
    {
        _users = users;
        _mutationGuards = users.Database.GetCollection<BsonDocument>("AdminMutationGuards");
    }

    public async Task<PagedResult<User>> SearchAsync(AdminUserQuery raw, CancellationToken token)
    {
        var query = raw.Normalize();
        var builder = Builders<User>.Filter;
        var filters = new List<FilterDefinition<User>>();
        if (query.Query is not null)
        {
            var regex = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query.Query), "i");
            filters.Add(builder.Or(builder.Regex(user => user.Username, regex), builder.Regex(user => user.Email, regex), builder.Regex(user => user.DisplayName, regex)));
        }
        if (query.Role is not null) filters.Add(builder.Eq(user => user.Role, query.Role));
        if (query.IsActive is not null) filters.Add(builder.Eq(user => user.IsActive, query.IsActive.Value));
        if (query.CreatedFrom is not null) filters.Add(builder.Gte(user => user.CreatedAt, query.CreatedFrom.Value));
        if (query.CreatedTo is not null) filters.Add(builder.Lt(user => user.CreatedAt, query.CreatedTo.Value));
        if (query.LastLoginFrom is not null) filters.Add(builder.Gte(user => user.LastLoginAt, query.LastLoginFrom.Value));
        if (query.LastLoginTo is not null) filters.Add(builder.Lt(user => user.LastLoginAt, query.LastLoginTo.Value));
        var filter = filters.Count == 0 ? builder.Empty : builder.And(filters);
        var total = await _users.CountDocumentsAsync(filter, cancellationToken: token);
        var offset = ((long)query.Page - 1) * query.PageSize;
        if (offset >= total) return new([], query.Page, query.PageSize, total);
        var field = query.Sort switch { "createdAt" => "CreatedAt", "lastLoginAt" => "LastLoginAt", "role" => "Role", _ => "Username" };
        var sort = query.Direction == "desc" ? Builders<User>.Sort.Descending(field).Descending(user => user.Id) : Builders<User>.Sort.Ascending(field).Ascending(user => user.Id);
        var users = offset <= int.MaxValue
            ? await _users.Find(filter).Sort(sort).Skip((int)offset).Limit(query.PageSize).ToListAsync(token)
            : await _users.Aggregate().Match(filter).Sort(sort).Skip(offset).Limit(query.PageSize).ToListAsync(token);
        return new(users, query.Page, query.PageSize, total);
    }

    public async Task<User?> FindByIdAsync(string id, CancellationToken token) =>
        ObjectId.TryParse(id, out var parsed) ? await _users.Find(user => user.Id == parsed.ToString()).FirstOrDefaultAsync(token) : null;
    public Task<long> CountActiveAdminsAsync(CancellationToken token) => _users.CountDocumentsAsync(user => user.Role == RoleNames.Admin && user.IsActive, cancellationToken: token);
    public async Task<bool> TrySetRoleAsync(string id, string role, DateTime at, CancellationToken token) => (await _users.UpdateOneAsync(user => user.Id == id, Builders<User>.Update.Set(user => user.Role, role).Set(user => user.UpdatedAt, at), cancellationToken: token)).ModifiedCount == 1;
    public async Task<bool> TrySetStatusAsync(string id, bool active, DateTime at, CancellationToken token) => (await _users.UpdateOneAsync(user => user.Id == id, Builders<User>.Update.Set(user => user.IsActive, active).Set(user => user.UpdatedAt, at), cancellationToken: token)).ModifiedCount == 1;

    public Task<AdminStoreMutationResult> SetRoleSafelyAsync(string actorId, string targetId, string role, DateTime at, CancellationToken token) =>
        MutateSafelyAsync(actorId, targetId, role, null, at, token);

    public Task<AdminStoreMutationResult> SetStatusSafelyAsync(string actorId, string targetId, bool active, DateTime at, CancellationToken token) =>
        MutateSafelyAsync(actorId, targetId, null, active, at, token);

    private async Task<AdminStoreMutationResult> MutateSafelyAsync(string actorId, string targetId, string? role, bool? active, DateTime at, CancellationToken token)
    {
        if (!ObjectId.TryParse(actorId, out var actorObjectId)) return AdminStoreMutationResult.ActorInvalid;
        if (!ObjectId.TryParse(targetId, out var targetObjectId)) return AdminStoreMutationResult.NotFound;
        var canonicalActorId = actorObjectId.ToString();
        var canonicalTargetId = targetObjectId.ToString();
        const string guardId = "active-admin-invariant";
        try
        {
            await _mutationGuards.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", guardId),
                Builders<BsonDocument>.Update.SetOnInsert("createdAt", DateTime.UtcNow).SetOnInsert("version", 0L),
                new UpdateOptions { IsUpsert = true },
                token);
            using var session = await _users.Database.Client.StartSessionAsync(cancellationToken: token);
            return await session.WithTransactionAsync(async (transaction, transactionToken) =>
            {
                await _mutationGuards.UpdateOneAsync(
                    transaction,
                    Builders<BsonDocument>.Filter.Eq("_id", guardId),
                    Builders<BsonDocument>.Update.Inc("version", 1L),
                    cancellationToken: transactionToken);

                var actor = await _users.Find(transaction, user => user.Id == canonicalActorId).FirstOrDefaultAsync(transactionToken);
                if (actor is null || !actor.IsActive || !string.Equals(actor.Role, RoleNames.Admin, StringComparison.Ordinal))
                    return new(AdminStoreMutationOutcome.ActorInvalid, actor?.Username);

                var target = await _users.Find(transaction, user => user.Id == canonicalTargetId).FirstOrDefaultAsync(transactionToken);
                if (target is null) return new(AdminStoreMutationOutcome.NotFound, actor.Username);

                var nextRole = role ?? target.Role;
                var nextActive = active ?? target.IsActive;
                var snapshot = new AdminStoreMutationResult(
                    AdminStoreMutationOutcome.Success,
                    actor.Username,
                    target.Username,
                    target.Role,
                    target.IsActive,
                    nextRole,
                    nextActive);
                if (canonicalActorId == canonicalTargetId && (nextRole != RoleNames.Admin || !nextActive))
                    return snapshot with { Outcome = AdminStoreMutationOutcome.SelfMutation };

                var removesAdmin = target.Role == RoleNames.Admin && target.IsActive && (nextRole != RoleNames.Admin || !nextActive);
                if (removesAdmin && await _users.CountDocumentsAsync(transaction, user => user.Role == RoleNames.Admin && user.IsActive, cancellationToken: transactionToken) <= 1)
                    return snapshot with { Outcome = AdminStoreMutationOutcome.LastAdmin };

                var update = Builders<User>.Update.Set(user => user.UpdatedAt, at);
                if (role is not null) update = update.Set(user => user.Role, role);
                if (active is not null) update = update.Set(user => user.IsActive, active.Value);
                var result = await _users.UpdateOneAsync(transaction, user => user.Id == canonicalTargetId, update, cancellationToken: transactionToken);
                return result.MatchedCount == 1 ? snapshot : snapshot with { Outcome = AdminStoreMutationOutcome.Conflict };
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (MongoConnectionException) { return AdminStoreMutationResult.Unavailable; }
        catch (TimeoutException) { return AdminStoreMutationResult.Unavailable; }
        catch (MongoException) { return AdminStoreMutationResult.Conflict; }
    }
}
