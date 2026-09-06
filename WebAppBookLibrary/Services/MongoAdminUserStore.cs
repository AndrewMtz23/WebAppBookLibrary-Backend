using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;

namespace WebAppBookLibrary.Services;

public sealed class MongoAdminUserStore(MongoDBService database) : IAdminUserStore
{
    private readonly IMongoCollection<User> _users = database.Users;

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
        var filter = filters.Count == 0 ? builder.Empty : builder.And(filters);
        var total = await _users.CountDocumentsAsync(filter, cancellationToken: token);
        var users = await _users.Find(filter).SortBy(user => user.Username).ThenBy(user => user.Id).Skip((query.Page - 1) * query.PageSize).Limit(query.PageSize).ToListAsync(token);
        return new(users, query.Page, query.PageSize, total);
    }

    public async Task<User?> FindByIdAsync(string id, CancellationToken token) => await _users.Find(user => user.Id == id).FirstOrDefaultAsync(token);
    public Task<long> CountActiveAdminsAsync(CancellationToken token) => _users.CountDocumentsAsync(user => user.Role == RoleNames.Admin && user.IsActive, cancellationToken: token);
    public async Task<bool> TrySetRoleAsync(string id, string role, DateTime at, CancellationToken token) => (await _users.UpdateOneAsync(user => user.Id == id, Builders<User>.Update.Set(user => user.Role, role).Set(user => user.UpdatedAt, at), cancellationToken: token)).ModifiedCount == 1;
    public async Task<bool> TrySetStatusAsync(string id, bool active, DateTime at, CancellationToken token) => (await _users.UpdateOneAsync(user => user.Id == id, Builders<User>.Update.Set(user => user.IsActive, active).Set(user => user.UpdatedAt, at), cancellationToken: token)).ModifiedCount == 1;

    public Task<AdminStoreMutationResult> SetRoleSafelyAsync(string actorId, string targetId, string role, DateTime at, CancellationToken token) =>
        MutateSafelyAsync(actorId, targetId, role, null, at, token);

    public Task<AdminStoreMutationResult> SetStatusSafelyAsync(string actorId, string targetId, bool active, DateTime at, CancellationToken token) =>
        MutateSafelyAsync(actorId, targetId, null, active, at, token);

    private async Task<AdminStoreMutationResult> MutateSafelyAsync(string actorId, string targetId, string? role, bool? active, DateTime at, CancellationToken token)
    {
        using var session = await _users.Database.Client.StartSessionAsync(cancellationToken: token);
        session.StartTransaction();
        try
        {
            var target = await _users.Find(session, user => user.Id == targetId).FirstOrDefaultAsync(token);
            if (target is null) { await session.AbortTransactionAsync(token); return AdminStoreMutationResult.NotFound; }
            if (actorId == targetId && ((role is not null && role != RoleNames.Admin) || active == false))
            { await session.AbortTransactionAsync(token); return AdminStoreMutationResult.SelfMutation; }
            var removesAdmin = target.Role == RoleNames.Admin && target.IsActive && ((role is not null && role != RoleNames.Admin) || active == false);
            if (removesAdmin && await _users.CountDocumentsAsync(session, user => user.Role == RoleNames.Admin && user.IsActive, cancellationToken: token) <= 1)
            { await session.AbortTransactionAsync(token); return AdminStoreMutationResult.LastAdmin; }
            var update = Builders<User>.Update.Set(user => user.UpdatedAt, at);
            if (role is not null) update = update.Set(user => user.Role, role);
            if (active is not null) update = update.Set(user => user.IsActive, active.Value);
            var result = await _users.UpdateOneAsync(session, user => user.Id == targetId, update, cancellationToken: token);
            if (result.MatchedCount != 1) { await session.AbortTransactionAsync(token); return AdminStoreMutationResult.Conflict; }
            await session.CommitTransactionAsync(token);
            return AdminStoreMutationResult.Success;
        }
        catch
        {
            if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None);
            return AdminStoreMutationResult.Conflict;
        }
    }
}
