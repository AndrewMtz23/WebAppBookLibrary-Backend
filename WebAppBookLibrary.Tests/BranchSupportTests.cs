using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Branches;
using WebAppBookLibrary.Services.Support;

namespace WebAppBookLibrary.Tests;

public sealed class BranchSupportTests
{
    [LocalMongoFact]
    public Task Digital_loan_support_accepts_owned_institutional_loans_but_not_another_readers() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        await db.GetCollection<BsonDocument>("BranchState").InsertOneAsync(new() { { "_id", "context" }, { "Status", "ready" } });
        var reader = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var other = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = reader.Id, BookId = ObjectId.GenerateNewId().ToString(), MediaType = "digital", Status = "active" };
        await f.Mongo.Loans.InsertOneAsync(loan);
        var store = new MongoSupportStore(f.Mongo, Options.Create(new SupportOptions()), TimeProvider.System, new(reader.Id, f.A.Id, false));
        var request = SupportPersistenceTests.Request() with { Category = "loan", LoanId = loan.Id };
        Assert.Equal(200, (await store.CreateAsync(reader.Id, request, default)).Status);
        var foreign = new MongoSupportStore(f.Mongo, Options.Create(new SupportOptions()), TimeProvider.System, new(other.Id, f.A.Id, false));
        Assert.Equal(404, (await foreign.CreateAsync(other.Id, request, default)).Status);
    });

    [LocalMongoFact]
    public Task Support_counts_details_writes_and_idempotency_respect_origin_branch() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        await db.GetCollection<BsonDocument>("BranchState").InsertOneAsync(new() { { "_id", "context" }, { "Status", "ready" } });
        var reader = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var a = new MongoSupportStore(f.Mongo, Options.Create(new SupportOptions()), TimeProvider.System, new(reader.Id, f.A.Id, false));
        var b = new MongoSupportStore(f.Mongo, Options.Create(new SupportOptions()), TimeProvider.System, new(reader.Id, f.B.Id, false));
        var request = SupportPersistenceTests.Request();
        var first = (await a.CreateAsync(reader.Id, request, default)).Value!;
        Assert.Equal(409, (await b.CreateAsync(reader.Id, request, default)).Status);
        var other = (await b.CreateAsync(reader.Id, SupportPersistenceTests.Request(), default)).Value!;
        var query = new SupportQueries(f.Mongo, branchContext: new(f.Staff.Id, f.A.Id, true));
        Assert.Equal(1, (await query.ListStaffAsync(f.Staff.Id, new(), default)).Value!.TotalItems);
        Assert.Equal(404, (await query.GetStaffAsync(f.Staff.Id, other.Id, 1, 20, default)).Status);
        var staff = new MongoSupportStore(f.Mongo, Options.Create(new SupportOptions()), TimeProvider.System, new(f.Staff.Id, f.A.Id, true));
        Assert.Equal(404, (await staff.AddNoteAsync(f.Staff.Id, other.Id, new("No cross branch note", 1), default)).Status);
        Assert.Equal(200, (await staff.AddNoteAsync(f.Staff.Id, first.Id, new("Local note", 1), default)).Status);
        Assert.Equal(f.B.Id, (await db.GetCollection<SupportTicket>("SupportTickets").Find(t => t.Id == other.Id).SingleAsync()).BranchId);
    });
}
