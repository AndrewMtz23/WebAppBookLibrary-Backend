using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Domain.Branches;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Branches;

public sealed class BranchStore(MongoDBService mongo, TimeProvider clock)
{
    private IMongoDatabase Db => mongo._database;
    private IMongoCollection<Branch> Branches => Db.GetCollection<Branch>("Branches");
    private IMongoCollection<BranchStaffAssignment> Assignments => Db.GetCollection<BranchStaffAssignment>("BranchStaffAssignments");
    private DateTime Now => new BsonDateTime(clock.GetUtcNow().UtcDateTime).ToUniversalTime();

    private async Task<BranchResult<T>> AdminTransaction<T>(string actor, Func<IClientSessionHandle, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (!ObjectId.TryParse(actor, out _)) return new(false, "branch_forbidden");
        try
        {
            using var session = await Db.Client.StartSessionAsync(cancellationToken: ct);
            var value = await session.WithTransactionAsync(async (tx, token) =>
            {
                if (!await mongo.Users.Find(tx, u => u.Id == actor && u.IsActive && u.Role == "admin").AnyAsync(token)) throw new BranchAccessException("branch_forbidden");
                await UserReferenceGuard.TouchAsync(mongo.Users, tx, actor, token);
                return await action(tx, token);
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct);
            return new(true, null, value);
        }
        catch (BranchAccessException ex) { return new(false, ex.Code); }
        catch (UserReferenceUnavailableException) { return new(false, "branch_forbidden"); }
        catch (MongoException ex) when (ex is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } or MongoCommandException { Code: 11000 }) { return new(false, "branch_duplicate"); }
    }

    public Task<BranchResult<Branch>> CreateAsync(string actor, CreateBranchRequest request, CancellationToken ct)
    {
        string code;
        try { code = BranchRules.NormalizeCode(request.Code); } catch (ArgumentException) { return Task.FromResult(new BranchResult<Branch>(false, "branch_invalid")); }
        if (!BranchRules.ValidName(request.Name) || !BranchRules.ValidTimeZone(request.TimeZone)) return Task.FromResult(new BranchResult<Branch>(false, "branch_invalid"));
        return AdminTransaction(actor, async (tx, token) =>
        {
            var organization = await Db.GetCollection<Organization>("Organizations").Find(tx, o => o.Id == "initial" && o.IsActive).FirstOrDefaultAsync(token);
            if (organization is null) throw new BranchAccessException("branch_migration_required");
            var branch = new Branch { Id = ObjectId.GenerateNewId().ToString(), Code = code, Name = request.Name.Trim(), TimeZone = request.TimeZone, CreatedAt = Now, UpdatedAt = Now };
            await Branches.InsertOneAsync(tx, branch, cancellationToken: token);
            await Audit(tx, actor, branch.Id, "branch_created", token);
            return branch;
        }, ct);
    }

    public Task<BranchResult<Branch>> UpdateAsync(string actor, string id, UpdateBranchRequest request, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _) || request.ExpectedVersion < 1 || !BranchRules.ValidName(request.Name) || !BranchRules.ValidTimeZone(request.TimeZone))
            return Task.FromResult(new BranchResult<Branch>(false, "branch_invalid"));
        return AdminTransaction(actor, async (tx, token) =>
        {
            var branch = await Branches.Find(tx, b => b.Id == id).FirstOrDefaultAsync(token) ?? throw new BranchAccessException("branch_not_found");
            if (branch.Version != request.ExpectedVersion) throw new BranchAccessException("version_conflict");
            if (!request.IsActive && await HasCommitmentsAsync(tx, id, token)) throw new BranchAccessException("branch_has_commitments");
            branch.Name = request.Name.Trim(); branch.TimeZone = request.TimeZone; branch.IsActive = request.IsActive; branch.Version++; branch.ReferenceVersion++; branch.UpdatedAt = Now;
            await Branches.ReplaceOneAsync(tx, b => b.Id == id, branch, cancellationToken: token);
            await Audit(tx, actor, id, "branch_updated", token);
            return branch;
        }, ct);
    }

    public Task<BranchResult<BranchStaffAssignment>> AssignAsync(string actor, string branchId, string userId, AssignBranchRequest request, CancellationToken ct)
    {
        if (!ObjectId.TryParse(branchId, out _) || !ObjectId.TryParse(userId, out _) || request.ExpectedVersion < 0)
            return Task.FromResult(new BranchResult<BranchStaffAssignment>(false, "branch_invalid"));
        return AdminTransaction(actor, async (tx, token) =>
        {
            if (!await Branches.Find(tx, b => b.Id == branchId).AnyAsync(token)) throw new BranchAccessException("branch_not_found");
            if (!await mongo.Users.Find(tx, u => u.Id == userId && u.IsActive && u.Role == "librarian").AnyAsync(token)) throw new BranchAccessException("branch_staff_invalid");
            await UserReferenceGuard.TouchAsync(mongo.Users, tx, userId, token);
            var assignment = await Assignments.Find(tx, a => a.UserId == userId && a.BranchId == branchId).FirstOrDefaultAsync(token);
            if ((assignment?.Version ?? 0) != request.ExpectedVersion) throw new BranchAccessException("version_conflict");
            if (assignment is null)
            {
                assignment = new() { Id = userId + ":" + branchId, UserId = userId, BranchId = branchId, IsActive = request.IsActive };
                await Assignments.InsertOneAsync(tx, assignment, cancellationToken: token);
            }
            else
            {
                assignment.IsActive = request.IsActive; assignment.Version++; assignment.ReferenceVersion++;
                await Assignments.ReplaceOneAsync(tx, a => a.Id == assignment.Id, assignment, cancellationToken: token);
            }
            await Audit(tx, actor, branchId, "branch_staff_updated", token);
            return assignment;
        }, ct);
    }

    public async Task<IReadOnlyList<Branch>> MineAsync(string actor, CancellationToken ct)
    {
        var user = ObjectId.TryParse(actor, out _) ? await mongo.Users.Find(u => u.Id == actor && u.IsActive).FirstOrDefaultAsync(ct) : null;
        if (user?.Role is not ("admin" or "librarian")) return [];
        if (user.Role == "admin") return await Branches.Find(b => true).SortBy(b => b.Code).ToListAsync(ct);
        var ids = await Assignments.Find(a => a.UserId == actor && a.IsActive).Project(a => a.BranchId).ToListAsync(ct);
        return await Branches.Find(b => ids.Contains(b.Id) && b.IsActive).SortBy(b => b.Code).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PublicBranch>> PublicAsync(CancellationToken ct) =>
        await Branches.Find(b => b.IsActive).SortBy(b => b.Code).Project(b => new PublicBranch(b.Id, b.Code, b.Name, b.TimeZone)).ToListAsync(ct);

    internal async Task<bool> HasCommitmentsAsync(IClientSessionHandle tx, string branchId, CancellationToken ct, string? bookId = null)
    {
        var loanFilter = new BsonDocument { { "BranchId", branchId }, { "$or", new BsonArray {
            new BsonDocument("Status", new BsonDocument("$in", new BsonArray { "active", "overdue" })),
            new BsonDocument { { "Status", new BsonDocument("$in", new BsonArray { BsonNull.Value, "" }) }, { "IsReturned", new BsonDocument("$ne", true) } }
        } } };
        if (bookId is not null) loanFilter.Add("BookId", bookId);
        if (await Db.GetCollection<BsonDocument>("Loans").Find(tx, loanFilter).AnyAsync(ct)) return true;
        foreach (var (collection, statuses) in new[] { ("PickupReservations", new[] { "ready" }), ("WaitlistEntries", new[] { "queued", "offered" }), ("RenewalRequests", new[] { "pending" }) })
        {
            var filter = new BsonDocument { { "BranchId", branchId }, { "Status", new BsonDocument("$in", new BsonArray(statuses)) } };
            if (bookId is not null) filter.Add("BookId", bookId);
            if (await Db.GetCollection<BsonDocument>(collection).Find(tx, filter).AnyAsync(ct)) return true;
        }
        return false;
    }

    private Task Audit(IClientSessionHandle tx, string actor, string branch, string action, CancellationToken ct) =>
        mongo.LogEntries.InsertOneAsync(tx, new LogEntry { Id = ObjectId.GenerateNewId().ToString(), Timestamp = Now, Level = "Information", EventType = action, ActorId = actor, TargetType = "branch", TargetId = branch, Action = action, Message = action }, cancellationToken: ct);
}
