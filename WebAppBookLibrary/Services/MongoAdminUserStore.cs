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
        MutateSafelyAsync(actorId, targetId, role, null, null, at, token);

    public Task<AdminStoreMutationResult> SetStatusSafelyAsync(string actorId, string targetId, bool active, DateTime at, CancellationToken token) =>
        MutateSafelyAsync(actorId, targetId, null, active, null, at, token);

    public Task<AdminStoreMutationResult> UpdateSafelyAsync(string actorId, string targetId, AdminUserUpdateCommand command, DateTime at, CancellationToken token) =>
        MutateSafelyAsync(actorId, targetId, null, null, command, at, token);

    public Task<AdminStoreMutationResult> DeletePermanentlyAsync(string actorId, string targetId, CancellationToken token) =>
        MutateSafelyAsync(actorId, targetId, null, null, null, DateTime.UtcNow, token, permanent: true);

    private async Task<AdminStoreMutationResult> MutateSafelyAsync(string actorId, string targetId, string? role, bool? active, AdminUserUpdateCommand? command, DateTime at, CancellationToken token, bool permanent = false)
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

                if (command is not null && target.UpdatedAt.ToUniversalTime() != command.ExpectedUpdatedAt)
                    return new(AdminStoreMutationOutcome.Conflict, actor.Username, target.Username, target.Role, target.IsActive, command.Role, command.IsActive);

                var nextRole = command?.Role ?? role ?? target.Role;
                var nextActive = command?.IsActive ?? active ?? target.IsActive;
                var snapshot = new AdminStoreMutationResult(
                    AdminStoreMutationOutcome.Success,
                    actor.Username,
                    target.Username,
                    target.Role,
                    target.IsActive,
                    nextRole,
                    nextActive);
                var changesOwnUsername = command is not null && !string.Equals(target.Username, command.Username, StringComparison.Ordinal);
                if (canonicalActorId == canonicalTargetId && (permanent || nextRole != RoleNames.Admin || !nextActive || changesOwnUsername))
                    return snapshot with { Outcome = AdminStoreMutationOutcome.SelfMutation };

                var removesAdmin = target.Role == RoleNames.Admin && target.IsActive && (permanent || nextRole != RoleNames.Admin || !nextActive);
                if (removesAdmin && await _users.CountDocumentsAsync(transaction, user => user.Role == RoleNames.Admin && user.IsActive, cancellationToken: transactionToken) <= 1)
                    return snapshot with { Outcome = AdminStoreMutationOutcome.LastAdmin };

                if (permanent)
                {
                    if (target.IsActive) return snapshot with { Outcome = AdminStoreMutationOutcome.MustBeInactive };
                    var loans = _users.Database.GetCollection<Loan>("Loans");
                    if (await loans.Find(transaction, loan => loan.UserId == canonicalTargetId).AnyAsync(transactionToken))
                        return snapshot with { Outcome = AdminStoreMutationOutcome.HasLoans };
                    // The delete conflicts with in-flight reference writes on the same user document.
                    var deleted = await _users.DeleteOneAsync(transaction, user => user.Id == canonicalTargetId, cancellationToken: transactionToken);
                    if (deleted.DeletedCount != 1) return snapshot with { Outcome = AdminStoreMutationOutcome.Conflict };
                    await _users.Database.GetCollection<Favorite>("Favorites").DeleteManyAsync(transaction,
                        favorite => favorite.UserId == canonicalTargetId, cancellationToken: transactionToken);
                    return snapshot;
                }

                // Return the same millisecond precision MongoDB persists for the next concurrency check.
                var updatedAt = new BsonDateTime(at).ToUniversalTime();
                var update = Builders<User>.Update.Set(user => user.UpdatedAt, updatedAt);
                if (command is not null)
                {
                    update = update
                        .Set(user => user.Username, command.Username)
                        .Set(user => user.NormalizedUsername, command.Username.ToUpperInvariant())
                        .Set(user => user.DisplayName, command.DisplayName)
                        .Set(user => user.Email, command.Email)
                        .Set(user => user.NormalizedEmail, command.Email.ToUpperInvariant())
                        .Set(user => user.AvatarUrl, command.AvatarUrl)
                        .Set(user => user.Role, command.Role)
                        .Set(user => user.IsActive, command.IsActive);
                }
                else
                {
                    if (role is not null) update = update.Set(user => user.Role, role);
                    if (active is not null) update = update.Set(user => user.IsActive, active.Value);
                }
                var result = await _users.UpdateOneAsync(transaction, user => user.Id == canonicalTargetId, update, cancellationToken: transactionToken);
                if (result.MatchedCount != 1) return snapshot with { Outcome = AdminStoreMutationOutcome.Conflict };
                if (command is null) return snapshot;
                target.Username = command.Username;
                target.NormalizedUsername = command.Username.ToUpperInvariant();
                target.DisplayName = command.DisplayName;
                target.Email = command.Email;
                target.NormalizedEmail = command.Email.ToUpperInvariant();
                target.AvatarUrl = command.AvatarUrl;
                target.Role = command.Role;
                target.IsActive = command.IsActive;
                target.UpdatedAt = updatedAt;
                return snapshot with { UpdatedUser = target };
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey) { return new(AdminStoreMutationOutcome.IdentityConflict); }
        catch (MongoCommandException exception) when (exception.Code == 11000) { return new(AdminStoreMutationOutcome.IdentityConflict); }
        catch (MongoConnectionException) { return AdminStoreMutationResult.Unavailable; }
        catch (TimeoutException) { return AdminStoreMutationResult.Unavailable; }
        catch (MongoException) { return AdminStoreMutationResult.Conflict; }
    }
}
