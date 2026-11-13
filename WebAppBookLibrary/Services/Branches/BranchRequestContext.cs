using System.Security.Claims;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Branches;

// Requested context is untrusted until ResolveAsync validates it against persisted state.
public sealed record BranchRequestContext(string? ActorId, string? BranchId, bool IsStaff)
{
    public static BranchRequestContext From(HttpContext? context) => new(
        context?.User.FindFirstValue(ClaimTypes.NameIdentifier), context?.Request.Headers["X-Branch-Id"].ToString() is { Length: > 0 } id ? id : null,
        context?.User.IsInRole("admin") == true || context?.User.IsInRole("librarian") == true);

    public static async Task<bool> EnabledAsync(IMongoDatabase db, CancellationToken ct) =>
        await db.GetCollection<BsonDocument>("BranchState").Find(new BsonDocument("_id", "context")).AnyAsync(ct);

    public static async Task<string?> ResolveAsync(MongoDBService mongo, BranchRequestContext? request, CancellationToken ct)
    {
        var state = await mongo._database.GetCollection<BsonDocument>("BranchState").Find(new BsonDocument("_id", "context")).FirstOrDefaultAsync(ct);
        if (state is null) return null;
        if (state.GetValue("Status", "").AsString != "ready") throw new BranchAccessException("branch_migration_in_progress");
        if (request?.BranchId is null || !ObjectId.TryParse(request.BranchId, out _)) throw new BranchAccessException("branch_required");
        if (request.IsStaff)
        {
            if (await new BranchAccessService(mongo).ResolveAsync(request.ActorId ?? "", request.BranchId, ct) is null) throw new BranchAccessException("branch_forbidden");
        }
        else
        {
            var branch = await mongo._database.GetCollection<Branch>("Branches").Find(b => b.Id == request.BranchId && b.IsActive).FirstOrDefaultAsync(ct);
            if (branch is null || !await mongo._database.GetCollection<Organization>("Organizations").Find(o => o.Id == branch.OrganizationId && o.IsActive).AnyAsync(ct))
                throw new BranchAccessException("branch_unavailable");
        }
        return request.BranchId;
    }
}
