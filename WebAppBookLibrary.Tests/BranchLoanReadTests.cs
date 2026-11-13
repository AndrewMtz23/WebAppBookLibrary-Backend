using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Tests;

public sealed class BranchLoanReadTests
{
    [LocalMongoFact]
    public Task Personal_loans_keep_book_and_origin_outside_selected_catalog_and_do_not_expose_another_reader() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        await db.GetCollection<BsonDocument>("BranchState").InsertOneAsync(new() { { "_id", "context" }, { "Status", "ready" } });
        var reader = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var other = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Exclusive origin title", MediaType = "physical", CoverUrl = "https://example.test/cover.jpg" };
        await f.Mongo.Books.InsertOneAsync(book);
        await f.Mongo.Loans.InsertManyAsync(new[] {
            new Loan { BookId = book.Id, UserId = reader.Id, MediaType = "physical", BranchId = f.A.Id, Status = "returned" },
            new Loan { BookId = book.Id, UserId = other.Id, MediaType = "physical", BranchId = f.A.Id, Status = "returned" }
        });
        var context = new BranchRequestContext(reader.Id, f.B.Id, false);
        var service = new LoanService(new MongoLoanStore(f.Mongo, branchContext: context), f.Mongo, null!, branchContext: context);
        var page = await service.SearchMineAsync(reader.Username, new(), default);
        var loan = Assert.Single(page!.Items);
        Assert.Equal(book.Title, loan.BookTitle);
        Assert.Equal(book.CoverUrl, loan.BookCoverUrl);
        Assert.Equal(f.A.Id, loan.BranchId);
        Assert.Equal(f.A.Name, loan.BranchName);
        Assert.Equal(reader.Id, loan.UserId);
    });

    [LocalMongoFact]
    public Task Digital_reservation_is_institutional_and_does_not_block_branch_closure() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        await db.GetCollection<BsonDocument>("BranchState").InsertOneAsync(new() { { "_id", "context" }, { "Status", "ready" } });
        var reader = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Digital shared title", MediaType = "digital", DigitalResourceUrl = "https://example.test/read" };
        await f.Mongo.Books.InsertOneAsync(book);
        await new BranchCatalogStore(f.Mongo, TimeProvider.System).SetAsync(f.Admin.Id, f.A.Id, book.Id, true, 0, default);
        var store = new MongoLoanStore(f.Mongo, branchContext: new(reader.Id, f.A.Id, false));
        var loan = new Loan { BookId = book.Id, UserId = reader.Id, MediaType = "digital", Status = "active" };
        await store.InsertLoanAsync(loan, default);
        Assert.Null((await f.Mongo.Loans.Find(l => l.Id == loan.Id).SingleAsync()).BranchId);
        Assert.True((await new BranchStore(f.Mongo, TimeProvider.System).UpdateAsync(f.Admin.Id, f.A.Id, new(f.A.Name, "UTC", false, 1), default)).Success);
        var staffAtB = new MongoLoanStore(f.Mongo, branchContext: new(f.Admin.Id, f.B.Id, true));
        Assert.Equal(loan.Id, Assert.Single((await staffAtB.SearchAsync(new WebAppBookLibrary.Contracts.Loans.LoanQuery().Normalize(), default)).Items).Id);
    });
}
