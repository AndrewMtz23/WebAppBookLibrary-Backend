using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Services;

public sealed partial class MongoBookStore
{
    private BranchRequestContext? _branchContext;
    private Task<string?> ResolveBranchAsync(CancellationToken ct) => BranchRequestContext.ResolveAsync(new MongoDBService(_books.Database), _branchContext, ct);

    private async Task<FilterDefinition<Book>> ContextFilterAsync(NormalizedBookQuery query, bool includeInactive, CancellationToken ct)
    {
        var branchId = await ResolveBranchAsync(ct);
        if (branchId is null) return RenderFilter(query, includeInactive);
        var entries = await _books.Database.GetCollection<BranchCatalogEntry>("BranchCatalog").Find(e => e.BranchId == branchId && (includeInactive || e.IsActive)).ToListAsync(ct);
        var builder = Builders<Book>.Filter;
        var filter = builder.In(b => b.Id, entries.Select(e => e.BookId)) & RenderFilter(query with { Available = null, LowStock = null, IsActive = null }, includeInactive);
        if (includeInactive && query.IsActive.HasValue)
        {
            var active = builder.In(b => b.Id, entries.Where(e => e.IsActive).Select(e => e.BookId)) & builder.Ne(b => b.IsActive, false);
            filter &= query.IsActive.Value ? active : builder.Not(active);
        }
        if (query.Available.HasValue || query.LowStock.HasValue)
        {
            var units = await _books.Database.GetCollection<BookCopy>("BookCopies").Aggregate()
                .Match(c => c.BranchId == branchId && c.Status == "available" && !c.InspectionPending && (c.Condition == "good" || c.Condition == "worn"))
                .Group(c => c.BookId, g => new { BookId = g.Key, Count = g.Count() }).ToListAsync(ct);
            var available = builder.Eq(b => b.MediaType, "digital") | builder.In(b => b.Id, units.Select(c => c.BookId)) |
                (builder.Ne(b => b.InventoryMode, "unit") & builder.Eq(b => b.InventoryBranchId, branchId) & builder.Gt(b => b.AvailableCopies, 0));
            if (query.Available.HasValue) filter &= query.Available.Value ? available : builder.Not(available);
            if (includeInactive && query.LowStock.HasValue)
            {
                var low = builder.Eq(b => b.IsActive, true) & builder.Eq(b => b.MediaType, "physical") &
                    (builder.In(b => b.Id, units.Where(c => c.Count == 1).Select(c => c.BookId)) |
                    (builder.Ne(b => b.InventoryMode, "unit") & builder.Eq(b => b.InventoryBranchId, branchId) & builder.Eq(b => b.AvailableCopies, 1)));
                filter &= query.LowStock.Value ? low : builder.Not(low);
            }
        }
        return filter;
    }

    private async Task ProjectBranchAsync(IEnumerable<Book> books, CancellationToken ct)
    {
        var branchId = await ResolveBranchAsync(ct);
        if (branchId is null) return;
        var rows = books.ToArray(); var ids = rows.Select(b => b.Id).ToArray();
        var memberships = await _books.Database.GetCollection<BranchCatalogEntry>("BranchCatalog").Find(e => e.BranchId == branchId && ids.Contains(e.BookId)).ToListAsync(ct);
        foreach (var row in rows) row.IsActive = row.IsActive && memberships.Any(e => e.BookId == row.Id && e.IsActive);
        var units = await _books.Database.GetCollection<BookCopy>("BookCopies").Find(c => c.BranchId == branchId && ids.Contains(c.BookId) && c.Status != "retired").ToListAsync(ct);
        foreach (var book in rows.Where(b => b.MediaType == "physical"))
        {
            if (book.InventoryMode == "unit")
            {
                book.TotalCopies = units.Count(c => c.BookId == book.Id);
                book.AvailableCopies = units.Count(c => c.BookId == book.Id && Domain.Copies.CopyRules.IsLendable(c));
            }
            else if (book.InventoryBranchId != branchId) { book.TotalCopies = 0; book.AvailableCopies = 0; }
            book.IsAvailable = book.AvailableCopies > 0;
        }
    }
}
