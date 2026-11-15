using MongoDB.Driver;
using MongoDB.Bson;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Branches;

public static class BranchIndexes
{
    public static async Task CreateAsync(IMongoDatabase db)
    {
        await db.GetCollection<BranchCatalogEntry>("BranchCatalog").Indexes.CreateOneAsync(new CreateIndexModel<BranchCatalogEntry>(
            Builders<BranchCatalogEntry>.IndexKeys.Ascending(x => x.BranchId).Ascending(x => x.BookId),
            new CreateIndexOptions { Name = "ux_branch_book", Unique = true }));
        await db.GetCollection<Branch>("Branches").Indexes.CreateOneAsync(new CreateIndexModel<Branch>(
            Builders<Branch>.IndexKeys.Ascending(x => x.OrganizationId).Ascending(x => x.Code),
            new CreateIndexOptions { Name = "ux_branch_code", Unique = true }));
        await db.GetCollection<BranchStaffAssignment>("BranchStaffAssignments").Indexes.CreateOneAsync(new CreateIndexModel<BranchStaffAssignment>(
            Builders<BranchStaffAssignment>.IndexKeys.Ascending(x => x.UserId).Ascending(x => x.BranchId),
            new CreateIndexOptions { Name = "ux_branch_staff", Unique = true }));
        foreach (var name in new[] { "Loans", "PickupReservations", "WaitlistEntries", "RenewalRequests", "BookCopies" })
            await db.GetCollection<BsonDocument>(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "BranchId", 1 }, { "BookId", 1 }, { "Status", 1 }, { "CreatedAt", 1 }, { "_id", 1 } },
                new CreateIndexOptions { Name = "ix_branch_book_status" }));
        await db.GetCollection<BranchCatalogEntry>("BranchCatalog").Indexes.CreateOneAsync(new CreateIndexModel<BranchCatalogEntry>(
            Builders<BranchCatalogEntry>.IndexKeys.Ascending(x => x.BookId).Ascending(x => x.IsActive),
            new CreateIndexOptions { Name = "ix_book_catalog_membership" }));
        await db.GetCollection<BookChangeRequest>("BookChangeRequests").Indexes.CreateOneAsync(new CreateIndexModel<BookChangeRequest>(
            Builders<BookChangeRequest>.IndexKeys.Ascending(x => x.BranchId).Ascending(x => x.RequestedBy).Descending(x => x.CreatedAt).Descending(x => x.Id),
            new CreateIndexOptions { Name = "ix_branch_requester_changes" }));
    }
}
