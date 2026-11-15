using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Branches;

public sealed class BranchAccessException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public sealed class BranchAccessService(MongoDBService mongo)
{
    private IMongoCollection<Branch> Branches => mongo._database.GetCollection<Branch>("Branches");
    private IMongoCollection<BranchStaffAssignment> Assignments => mongo._database.GetCollection<BranchStaffAssignment>("BranchStaffAssignments");

    public async Task<BranchAccess?> ResolveAsync(string actorId, string branchId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(actorId, out _) || !ObjectId.TryParse(branchId, out _)) return null;
        var user = await mongo.Users.Find(u => u.Id == actorId && u.IsActive).FirstOrDefaultAsync(ct);
        if (user?.Role is not ("admin" or "librarian")) return null;
        var branch = await Branches.Find(b => b.Id == branchId && b.IsActive).FirstOrDefaultAsync(ct);
        if (branch is null) return null;
        if (!await mongo._database.GetCollection<Organization>("Organizations").Find(o => o.Id == branch.OrganizationId && o.IsActive).AnyAsync(ct)) return null;
        if (user.Role != "admin" && !await Assignments.Find(a => a.UserId == actorId && a.BranchId == branchId && a.IsActive).AnyAsync(ct)) return null;
        return new(branch, actorId, user.Role == "admin");
    }

    public async Task<BranchAccess> ValidateForMutationAsync(IClientSessionHandle tx, string actorId, string branchId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(actorId, out _) || !ObjectId.TryParse(branchId, out _)) throw new BranchAccessException("branch_forbidden");
        var user = await mongo.Users.Find(tx, u => u.Id == actorId && u.IsActive).FirstOrDefaultAsync(ct);
        if (user?.Role is not ("admin" or "librarian")) throw new BranchAccessException("branch_forbidden");
        await UserReferenceGuard.TouchAsync(mongo.Users, tx, actorId, ct);
        var branch = await TouchActiveAsync(tx, branchId, ct);
        if (user.Role != "admin")
        {
            var updated = await Assignments.UpdateOneAsync(tx, a => a.UserId == actorId && a.BranchId == branchId && a.IsActive,
                Builders<BranchStaffAssignment>.Update.Inc(a => a.ReferenceVersion, 1), cancellationToken: ct);
            if (updated.MatchedCount != 1) throw new BranchAccessException("branch_forbidden");
        }
        return new(branch, actorId, user.Role == "admin");
    }

    public async Task<Branch> TouchActiveAsync(IClientSessionHandle tx, string branchId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(branchId, out _)) throw new BranchAccessException("branch_required");
        var branch = await Branches.FindOneAndUpdateAsync(tx, b => b.Id == branchId && b.IsActive,
            Builders<Branch>.Update.Inc(b => b.ReferenceVersion, 1), new() { ReturnDocument = ReturnDocument.After }, ct);
        if (branch is null) throw new BranchAccessException("branch_unavailable");
        if (!await mongo._database.GetCollection<Organization>("Organizations").Find(tx, o => o.Id == branch.OrganizationId && o.IsActive).AnyAsync(ct))
            throw new BranchAccessException("branch_unavailable");
        return branch;
    }
}
