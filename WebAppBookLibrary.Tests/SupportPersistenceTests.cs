using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Support;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Support;

namespace WebAppBookLibrary.Tests;

public sealed class SupportPersistenceTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
    internal static CreateSupportTicket Request(string category = "loan", string? key = null) => new(category, "Private subject", "Private description", null, null, key ?? Guid.NewGuid().ToString());
    internal static async Task<User> UserAsync(MongoDBService db, string role)
    {
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Role = role, Username = Guid.NewGuid().ToString(), Email = Guid.NewGuid() + "@example.invalid", IsActive = true };
        await db.Users.InsertOneAsync(user); return user;
    }

    [LocalMongoFact]
    public Task Tickets_are_private_and_staff_scope_applies_to_counts_details_and_notes() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new Clock();
        var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), clock);
        var query = new SupportQueries(f.Db);
        var admin = await UserAsync(f.Db, "admin"); var librarian = await UserAsync(f.Db, "librarian");
        var loan = (await store.CreateAsync(f.User.Id, Request(), default)).Value!;
        var account = (await store.CreateAsync(f.User.Id, Request("account"), default)).Value!;
        Assert.Equal(404, (await query.GetMineAsync(admin.Id, loan.Id, 1, 20, default)).Status);
        Assert.Equal(404, (await query.GetStaffAsync(librarian.Id, account.Id, 1, 20, default)).Status);
        Assert.Equal(1, (await query.ListStaffAsync(librarian.Id, new(), default)).Value!.TotalItems);
        Assert.Equal(200, (await store.AddNoteAsync(admin.Id, loan.Id, new("Secret internal note", 1), default)).Status);
        var own = (await query.GetMineAsync(f.User.Id, loan.Id, 1, 20, default)).Value!;
        Assert.Empty(own.Replies.Items);
        Assert.DoesNotContain("Secret", System.Text.Json.JsonSerializer.Serialize(own));
        Assert.Single((await query.GetStaffAsync(admin.Id, loan.Id, 1, 20, default)).Value!.Notes.Items);
        Assert.Equal(403, (await query.ListStaffAsync(f.User.Id, new(), default)).Status);
    });

    [LocalMongoFact]
    public Task Creation_is_idempotent_and_concurrent_versions_have_one_winner() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new Clock();
        var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), clock);
        var request = Request();
        var creates = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.CreateAsync(f.User.Id, request, default)));
        Assert.All(creates, r => Assert.Equal(200, r.Status));
        Assert.Equal(creates[0].Value!.Id, creates[1].Value!.Id);
        Assert.Equal(409, (await store.CreateAsync(f.User.Id, request with { Subject = "Different" }, default)).Status);
        var id = creates[0].Value!.Id;
        var replies = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.ReplyAsync(f.User.Id, id, new("Reply", 1), false, default)));
        Assert.Equal(new[] { 200, 409 }, replies.Select(r => r.Status).Order());
        Assert.Single((await new SupportQueries(f.Db).GetMineAsync(f.User.Id, id, 1, 20, default)).Value!.Replies.Items);
    });

    [LocalMongoFact]
    public Task Quotas_and_current_account_status_are_enforced() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new Clock();
        var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => store.CreateAsync(f.User.Id, Request(), default)));
        Assert.Equal(3, results.Count(r => r.Status == 200)); Assert.Equal(2, results.Count(r => r.Status == 429));
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(200, (await store.CreateAsync(f.User.Id, Request(), default)).Status);
        Assert.Equal(200, (await store.CreateAsync(f.User.Id, Request(), default)).Status);
        Assert.Equal(429, (await store.CreateAsync(f.User.Id, Request(), default)).Status);
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.User.Id, Builders<User>.Update.Set(u => u.IsActive, false));
        Assert.Equal(401, (await store.CreateAsync(f.User.Id, Request(), default)).Status);
    });
}
