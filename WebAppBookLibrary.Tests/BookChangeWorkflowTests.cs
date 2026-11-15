using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Branches;
using WebAppBookLibrary.Services.Books;

namespace WebAppBookLibrary.Tests;

public sealed class BookChangeWorkflowTests
{
    internal static BookWriteRequest Draft(string title = "Título original") => new() { Title = title, Authors = ["Autora"], Description = "Descripción bibliográfica suficiente para la prueba.", Genres = ["Ficción"], TotalCopies = 0 };

    [LocalMongoFact]
    public Task Librarian_creates_and_edits_exclusive_but_shared_changes_require_admin_decision() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db); await SeedCategory(f.Mongo); var service = new BookChangeService(f.Mongo, TimeProvider.System);
        var created = await service.CreateAsync(f.Staff.Id, f.A.Id, Draft(), default);
        Assert.True(created.Success); var id = created.Value!.Id;
        Assert.True((await service.EditAsync(f.Staff.Id, f.A.Id, id, Draft("Corrección local"), 1, default)).Success);
        Assert.True((await new BranchCatalogStore(f.Mongo, TimeProvider.System).SetAsync(f.Admin.Id, f.B.Id, id, true, 0, default)).Success);
        Assert.Equal("book_approval_required", (await service.EditAsync(f.Staff.Id, f.A.Id, id, Draft("No permitido"), 2, default)).ErrorCode);
        var proposal = await service.ProposeAsync(f.Staff.Id, f.A.Id, id, Draft("Propuesta compartida"), 2, default);
        Assert.True(proposal.Success);
        Assert.Equal("branch_forbidden", (await service.DecideAsync(f.Staff.Id, proposal.Value!.Id, true, 1, default)).ErrorCode);
        Assert.True((await service.DecideAsync(f.Admin.Id, proposal.Value!.Id, false, 1, default)).Success);
        Assert.Equal("Corrección local", (await f.Mongo.Books.Find(b => b.Id == id).SingleAsync()).Title);
        var approved = (await service.ProposeAsync(f.Staff.Id, f.A.Id, id, Draft("Título aprobado"), 2, default)).Value!;
        Assert.True((await service.DecideAsync(f.Admin.Id, approved.Id, true, 1, default)).Success);
        Assert.Equal("Título aprobado", (await f.Mongo.Books.Find(b => b.Id == id).SingleAsync()).Title);
        Assert.Equal("version_conflict", (await service.DecideAsync(f.Admin.Id, approved.Id, false, 1, default)).ErrorCode);
        Assert.Equal(3, await db.GetCollection<BookMetadataHistoryEntry>("BookMetadataHistory").CountDocumentsAsync(h => h.BookId == id));
        var notices = await db.GetCollection<Notification>("Notifications").Find(n => n.UserId == f.Staff.Id).ToListAsync();
        Assert.Equal(2, notices.Count);
        Assert.Contains(notices, n => n.Type == "book_change_rejected");
        Assert.Contains(notices, n => n.Type == "book_change_approved");
        Assert.All(notices, n => { Assert.Equal(id, n.TargetId); Assert.Equal(f.A.Id, n.BranchId); });
    });

    [LocalMongoFact]
    public Task Approval_cannot_overwrite_a_newer_edit_or_change_inventory() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db); await SeedCategory(f.Mongo); var service = new BookChangeService(f.Mongo, TimeProvider.System);
        var book = (await service.CreateAsync(f.Staff.Id, f.A.Id, Draft(), default)).Value!;
        var request = (await service.ProposeAsync(f.Staff.Id, f.A.Id, book.Id, Draft("Propuesta"), 1, default)).Value!;
        Assert.True((await service.EditAsync(f.Admin.Id, f.A.Id, book.Id, Draft("Edición posterior"), 1, default)).Success);
        Assert.Equal("book_version_conflict", (await service.DecideAsync(f.Admin.Id, request.Id, true, 1, default)).ErrorCode);
        Assert.Equal("book_inventory_not_metadata", (await service.ProposeAsync(f.Staff.Id, f.A.Id, book.Id, Draft() with { TotalCopies = 20 }, 2, default)).ErrorCode);
        Assert.Equal("Edición posterior", (await f.Mongo.Books.Find(b => b.Id == book.Id).SingleAsync()).Title);
        Assert.Equal("pending", (await db.GetCollection<BookChangeRequest>("BookChangeRequests").Find(r => r.Id == request.Id).SingleAsync()).Status);
    });
    private static Task SeedCategory(WebAppBookLibrary.Services.MongoDBService mongo) => mongo.Categories.InsertOneAsync(new Category {
        Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(), Name = "Ficción", NormalizedName = WebAppBookLibrary.Domain.Categories.CategoryRules.NormalizeName("Ficción"), Slug = "ficcion", IsActive = true
    });
}
