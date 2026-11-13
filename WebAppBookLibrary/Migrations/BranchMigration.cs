using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Domain.Branches;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Migrations;

public sealed record BranchMigrationInput(string OrganizationName, string Code, string Name, string TimeZone);
public sealed record BranchMigrationPreview(bool Eligible, string Fingerprint, string BranchId, bool AlreadyApplied, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> Errors);

// Offline, bounded atomic conversion. No production connection is selected implicitly.
public sealed class BranchMigration(MongoDBService mongo, TimeProvider clock)
{
    private const string InitialBranchId = "000000000000000000000001";
    private static readonly string[] Collections = ["Books", "Users", "BookCopies", "Loans", "PickupReservations", "WaitlistEntries", "RenewalRequests", "CopyHistory", "CirculationHistory", "Favorites", "ReadingEntries", "Reviews", "SupportTickets", "Branches", "BranchCatalog", "BranchStaffAssignments", "Organizations"];
    private static readonly string[] ContextCollections = ["BookCopies", "Loans", "PickupReservations", "WaitlistEntries", "RenewalRequests", "CopyHistory", "CirculationHistory"];

    public async Task<BranchMigrationPreview> PreviewAsync(BranchMigrationInput input, CancellationToken ct)
    {
        using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync((tx, token) => Inspect(tx, input, token), new TransactionOptions(readConcern: ReadConcern.Snapshot), ct);
    }

    private async Task<BranchMigrationPreview> Inspect(IClientSessionHandle tx, BranchMigrationInput input, CancellationToken ct)
    {
        var errors = new List<string>();
        try { BranchRules.NormalizeCode(input.Code); } catch (ArgumentException) { errors.Add("invalid_code"); }
        if (!BranchRules.ValidName(input.Name) || !BranchRules.ValidName(input.OrganizationName) || !BranchRules.ValidTimeZone(input.TimeZone)) errors.Add("invalid_initial_branch");
        var rows = new Dictionary<string, List<BsonDocument>>();
        var serialized = new StringBuilder(JsonSerializer.Serialize(input));
        var bytes = 0;
        foreach (var name in Collections)
        {
            var documents = await mongo._database.GetCollection<BsonDocument>(name).Find(tx, FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).Limit(5001).ToListAsync(ct);
            rows[name] = documents;
            if (documents.Count > 5000) errors.Add("migration_batch_limit:" + name);
            foreach (var document in documents) bytes += document.ToBson().Length;
            serialized.Append(name);
            foreach (var document in documents) serialized.Append(document.ToJson());
        }
        if (bytes > 8 * 1024 * 1024) errors.Add("migration_size_limit");
        var applied = await mongo._database.GetCollection<BsonDocument>("BranchState").Find(tx, new BsonDocument("_id", "context")).FirstOrDefaultAsync(ct);
        var already = applied?.GetValue("Status", "").AsString == "ready";
        if (!already && (rows["Branches"].Count != 0 || rows["Organizations"].Count != 0)) errors.Add("initial_context_exists");
        var books = rows["Books"].ToDictionary(d => d["_id"].ToString()!, d => d);
        var users = rows["Users"].Select(d => d["_id"].ToString()!).ToHashSet();
        if (!rows["Users"].Any(u => u.GetValue("Role", "").AsString == "admin" && u.GetValue("IsActive", true).ToBoolean())) errors.Add("active_admin_required");
        foreach (var name in ContextCollections.Concat(["Favorites", "ReadingEntries", "Reviews"]))
        foreach (var row in rows[name])
        {
            var bookId = row.GetValue("BookId", "").ToString()!;
            if (!books.ContainsKey(bookId)) errors.Add("orphan_book:" + name + ":" + row["_id"]);
            if (row.TryGetValue("UserId", out var user) && !user.IsBsonNull && user.ToString() is { Length: > 0 } userId && !users.Contains(userId))
                errors.Add("orphan_user:" + name + ":" + row["_id"]);
            if (!already && row.TryGetValue("BranchId", out var branch) && !branch.IsBsonNull && branch.AsString != InitialBranchId) errors.Add("conflicting_branch:" + name);
        }
        var references = rows.ToDictionary(pair => pair.Key, pair => pair.Value.ToDictionary(d => d["_id"].ToString()!, d => d));
        void Reference(string collection, BsonDocument row, string field, string target)
        {
            if (!row.TryGetValue(field, out var value) || value.IsBsonNull || value.ToString() is not { Length: > 0 } id) return;
            if (!references[target].TryGetValue(id, out var linked)) errors.Add($"orphan_{field}:{collection}:{row["_id"]}");
            else if (row.TryGetValue("BookId", out var book) && linked.TryGetValue("BookId", out var linkedBook) && book != linkedBook)
                errors.Add($"conflicting_{field}:{collection}:{row["_id"]}");
        }
        foreach (var name in ContextCollections)
        foreach (var row in rows[name])
        {
            Reference(name, row, "CopyId", "BookCopies");
            Reference(name, row, "LoanId", "Loans");
            Reference(name, row, "PickupReservationId", "PickupReservations");
            Reference(name, row, "WaitlistEntryId", "WaitlistEntries");
            if (name == "BookCopies")
            {
                var type = row.GetValue("ActiveAssignmentType", BsonNull.Value).ToString();
                if (type is "loan" or "pickup") Reference(name, row, "ActiveAssignmentId", type == "loan" ? "Loans" : "PickupReservations");
            }
        }
        foreach (var row in rows["SupportTickets"])
        {
            Reference("SupportTickets", row, "BookId", "Books");
            Reference("SupportTickets", row, "LoanId", "Loans");
            Reference("SupportTickets", row, "RequesterId", "Users");
        }
        if (errors.Count == 0)
        {
            var physical = await mongo.Books.Find(tx, b => b.MediaType == "physical").ToListAsync(ct);
            foreach (var book in physical)
            {
                var good = book.InventoryMode == "unit"
                    ? await new Services.Copies.CopyAllocation(mongo, clock).ConsistentAsync(tx, book, ct)
                    : book.TotalCopies >= 0 && book.AvailableCopies >= 0 && book.AvailableCopies + rows["PickupReservations"].Count(p => p.GetValue("BookId", "") == book.Id && p.GetValue("Status", "") == "ready") + rows["Loans"].Count(l => l.GetValue("BookId", "") == book.Id && IsOpen(l)) == book.TotalCopies;
                if (!good) errors.Add("inventory_inconsistent:" + book.Id);
            }
        }
        return new(errors.Count == 0, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized.ToString()))), InitialBranchId, already, rows.ToDictionary(x => x.Key, x => x.Value.Count), errors);
    }

    private static bool IsOpen(BsonDocument loan) => loan.GetValue("Status", "").ToString() is "active" or "overdue" ||
        (loan.GetValue("Status", "").IsBsonNull || loan.GetValue("Status", "").ToString() == "") && !loan.GetValue("IsReturned", false).ToBoolean();

    public async Task<BranchResult<BranchMigrationPreview>> ApplyAsync(BranchMigrationInput input, string expectedFingerprint, string actor, CancellationToken ct)
    {
        if (!ObjectId.TryParse(actor, out _)) return new(false, "branch_forbidden");
        await BranchIndexes.CreateAsync(mongo._database);
        try
        {
            using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
            return await session.WithTransactionAsync(async (tx, token) =>
            {
                if (!await mongo.Users.Find(tx, u => u.Id == actor && u.IsActive && u.Role == "admin").AnyAsync(token)) throw new BranchAccessException("branch_forbidden");
                var preview = await Inspect(tx, input, token);
                if (preview.Fingerprint != expectedFingerprint) throw new BranchAccessException("migration_preview_stale");
                if (!preview.Eligible) throw new BranchAccessException("migration_ineligible");
                if (preview.AlreadyApplied) return new BranchResult<BranchMigrationPreview>(true, null, preview);
                var now = new BsonDateTime(clock.GetUtcNow().UtcDateTime).ToUniversalTime();
                await mongo._database.GetCollection<Organization>("Organizations").InsertOneAsync(tx, new() { Name = input.OrganizationName.Trim() }, cancellationToken: token);
                await mongo._database.GetCollection<Branch>("Branches").InsertOneAsync(tx, new() { Id = InitialBranchId, Name = input.Name.Trim(), Code = BranchRules.NormalizeCode(input.Code), TimeZone = input.TimeZone, CreatedAt = now, UpdatedAt = now }, cancellationToken: token);
                var books = await mongo.Books.Find(tx, FilterDefinition<Book>.Empty).ToListAsync(token);
                foreach (var book in books)
                {
                    if (book.MediaType == "physical") await mongo.Books.UpdateOneAsync(tx, b => b.Id == book.Id, Builders<Book>.Update.Set(b => b.InventoryBranchId, InitialBranchId).Inc(b => b.ReferenceVersion, 1), cancellationToken: token);
                    await mongo._database.GetCollection<BranchCatalogEntry>("BranchCatalog").InsertOneAsync(tx, new() { Id = InitialBranchId + ":" + book.Id, BookId = book.Id, BranchId = InitialBranchId, IsActive = book.IsActive, UpdatedAt = now }, cancellationToken: token);
                }
                var physicalIds = books.Where(b => b.MediaType == "physical").Select(b => b.Id).ToArray();
                foreach (var name in ContextCollections)
                    await mongo._database.GetCollection<BsonDocument>(name).UpdateManyAsync(tx, new BsonDocument("BookId", new BsonDocument("$in", new BsonArray(physicalIds))), new BsonDocument("$set", new BsonDocument("BranchId", InitialBranchId)), cancellationToken: token);
                await mongo._database.GetCollection<SupportTicket>("SupportTickets").UpdateManyAsync(tx, t => t.Category == "loan" || t.Category == "catalog", Builders<SupportTicket>.Update.Set(t => t.BranchId, InitialBranchId), cancellationToken: token);
                var staff = await mongo.Users.Find(tx, u => u.Role == "librarian").ToListAsync(token);
                foreach (var user in staff)
                    await mongo._database.GetCollection<BranchStaffAssignment>("BranchStaffAssignments").InsertOneAsync(tx, new() { Id = user.Id + ":" + InitialBranchId, UserId = user.Id, BranchId = InitialBranchId, IsActive = user.IsActive }, cancellationToken: token);
                await mongo._database.GetCollection<BsonDocument>("BranchState").InsertOneAsync(tx, new() { { "_id", "context" }, { "Status", "ready" }, { "InitialBranchId", InitialBranchId }, { "AppliedAt", now }, { "ActorId", actor } }, cancellationToken: token);
                await UserReferenceGuard.TouchAsync(mongo.Users, tx, actor, token);
                return new BranchResult<BranchMigrationPreview>(true, null, preview);
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct);
        }
        catch (BranchAccessException ex) { return new(false, ex.Code); }
        catch (UserReferenceUnavailableException) { return new(false, "branch_forbidden"); }
    }
}
