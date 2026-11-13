using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Services;

public sealed partial class MongoCirculationStore
{
    private async Task<string?> BranchNameAsync(string? id, CancellationToken ct) => id is null ? null :
        await _db.GetCollection<Branch>("Branches").Find(b => b.Id == id).Project(b => b.Name).FirstOrDefaultAsync(ct);
    private async Task<string?> ValidateBranchBookAsync(IClientSessionHandle tx, Book book, string? branchId, CancellationToken ct)
    {
        if (branchId is null) return book.InventoryBranchId is null ? null : "branch_required";
        try
        {
            await new BranchAccessService(new(_db)).TouchActiveAsync(tx, branchId, ct);
            if (!await _db.GetCollection<BranchCatalogEntry>("BranchCatalog").Find(tx, e => e.BookId == book.Id && e.BranchId == branchId && e.IsActive).AnyAsync(ct)) return "book_not_found";
            if (book.InventoryMode != "unit" && book.InventoryBranchId != branchId) return "copy_conversion_required";
            return null;
        }
        catch (BranchAccessException ex) { return ex.Code; }
    }

    private Task<long> BranchAvailableAsync(IClientSessionHandle tx, Book book, string? branchId, CancellationToken ct) =>
        book.InventoryMode == "unit" && branchId is not null
            ? _db.GetCollection<BookCopy>("BookCopies").CountDocumentsAsync(tx, c => c.BookId == book.Id && c.BranchId == branchId && c.Status == "available" && !c.InspectionPending && (c.Condition == "good" || c.Condition == "worn") && c.ActiveAssignmentId == null, cancellationToken: ct)
            : Task.FromResult((long)(book.AvailableCopies ?? 0));

    private async Task<string?> ValidateStaffBranchAsync(IClientSessionHandle tx, string actor, string? resourceBranchId, string? requestedBranchId, CancellationToken ct)
    {
        if (resourceBranchId is null && requestedBranchId is null) return null;
        if (requestedBranchId is null) return "branch_required";
        if (resourceBranchId != requestedBranchId) return "branch_resource_not_found";
        try { await new BranchAccessService(new(_db)).ValidateForMutationAsync(tx, actor, requestedBranchId, ct); return null; }
        catch (BranchAccessException ex) { return ex.Code; }
    }
}
