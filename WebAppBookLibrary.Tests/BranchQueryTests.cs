using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Tests;

public sealed class BranchQueryTests
{
    [LocalMongoFact]
    public Task Institutional_catalog_search_finds_normalized_isbn_in_another_branch() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Existing edition", Isbn = "9781234567890", MediaType = "digital" };
        await f.Mongo.Books.InsertOneAsync(book);
        var controller = new WebAppBookLibrary.Controllers.BranchCatalogController(f.Mongo, new BranchCatalogStore(f.Mongo, TimeProvider.System));
        controller.ControllerContext = new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, f.Staff.Id) }, "test")) } };
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Search(f.A.Id, "978-1234567890"));
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Value));
        Assert.Equal(1, json.RootElement.GetProperty("totalItems").GetInt32());
    });

    [LocalMongoFact]
    public Task Staff_loan_queries_filter_before_counting_and_do_not_expose_other_branches() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        await db.GetCollection<BsonDocument>("BranchState").InsertOneAsync(new() { { "_id", "context" }, { "Status", "ready" } });
        var a = new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = ObjectId.GenerateNewId().ToString(), UserId = f.Admin.Id, BranchId = f.A.Id, Status = "active", MediaType = "physical", LoanDate = DateTime.UtcNow };
        var b = new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = a.BookId, UserId = f.Staff.Id, BranchId = f.B.Id, Status = "active", MediaType = "physical", LoanDate = DateTime.UtcNow };
        await f.Mongo.Loans.InsertManyAsync([a, b]);
        var store = new MongoLoanStore(f.Mongo, branchContext: new(f.Staff.Id, f.A.Id, true));
        var page = await store.SearchAsync(new WebAppBookLibrary.Contracts.Loans.LoanQuery().Normalize(), default);
        Assert.Equal(1, page.TotalItems);
        Assert.Equal(a.Id, Assert.Single(page.Items).Id);
        Assert.Null(await store.FindDetailAsync(b.Id, default));
        var forbidden = new MongoLoanStore(f.Mongo, branchContext: new(f.Staff.Id, f.B.Id, true));
        await Assert.ThrowsAsync<BranchAccessException>(() => forbidden.FindDetailAsync(b.Id, default));
    });

    [LocalMongoFact]
    public Task Queries_apply_catalog_membership_before_counting_and_reject_unassigned_staff() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Only A", MediaType = "physical", InventoryMode = "unit", InventoryBranchId = f.A.Id, TotalCopies = 1, AvailableCopies = 1 };
        await f.Mongo.Books.InsertOneAsync(book);
        await new BranchCatalogStore(f.Mongo, TimeProvider.System).SetAsync(f.Admin.Id, f.A.Id, book.Id, true, 0, default);
        await db.GetCollection<BookCopy>("BookCopies").InsertOneAsync(new() { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id, BranchId = f.A.Id, Status = "available", Condition = "good", InspectionPending = false });
        await db.GetCollection<BsonDocument>("BranchState").InsertOneAsync(new() { { "_id", "context" }, { "Status", "ready" } });
        var a = new MongoBookStore(f.Mongo, new BranchRequestContext(null, f.A.Id, false));
        var b = new MongoBookStore(f.Mongo, new BranchRequestContext(null, f.B.Id, false));
        Assert.Equal(1, (await a.SearchAsync(new BookQuery().Normalize(), false, null, default)).TotalItems);
        Assert.Equal(0, (await b.SearchAsync(new BookQuery().Normalize(), false, null, default)).TotalItems);
        Assert.Null(await b.FindCatalogEntryAsync(book.Id, false, null, default));
        Assert.Null(await b.FindByIdAsync(book.Id, default));
        var forbidden = new MongoBookStore(f.Mongo, new BranchRequestContext(f.Staff.Id, f.B.Id, true));
        Assert.Equal("branch_forbidden", (await Assert.ThrowsAsync<BranchAccessException>(() => forbidden.SearchAsync(new BookQuery().Normalize(), true, null, default))).Code);
        Assert.Equal("branch_required", (await Assert.ThrowsAsync<BranchAccessException>(() => new MongoBookStore(f.Mongo).SearchAsync(new BookQuery().Normalize(), false, null, default))).Code);
    });
}
