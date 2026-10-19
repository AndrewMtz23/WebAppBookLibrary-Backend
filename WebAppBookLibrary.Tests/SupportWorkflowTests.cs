using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Support;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Support;

namespace WebAppBookLibrary.Tests;
public sealed class SupportWorkflowTests
{
    [LocalMongoFact]
    public Task Concurrent_messages_on_different_tickets_share_the_actor_quota() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new SupportPersistenceTests.Clock();
        var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions { MessagesPerHour = 1 }), clock);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var first = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Value!;
        var second = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Value!;
        var results = await Task.WhenAll(store.AddNoteAsync(admin.Id, first.Id, new("First", 1), default), store.ReplyAsync(admin.Id, second.Id, new("Second", 1), true, default));
        Assert.Equal(new[] { 200, 429 }, results.Select(r => r.Status).Order());
    });

    [LocalMongoFact]
    public Task Deletion_racing_staff_reply_leaves_no_orphan_messages_or_notices() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new SupportPersistenceTests.Clock();
        var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), clock);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var users = new MongoAdminUserStore(f.Db);
        for (var i = 0; i < 4; i++) {
            var reader = await SupportPersistenceTests.UserAsync(f.Db, "user");
            var ticket = (await store.CreateAsync(reader.Id, SupportPersistenceTests.Request(), default)).Value!;
            await users.SetStatusSafelyAsync(admin.Id, reader.Id, false, clock.Now, default);
            var deletion = users.DeletePermanentlyAsync(admin.Id, reader.Id, default);
            var reply = store.ReplyAsync(admin.Id, ticket.Id, new("Concurrent reply", 1), true, default);
            await Task.WhenAll(deletion, reply);
            Assert.Equal(AdminStoreMutationOutcome.Success, (await deletion).Outcome);
            Assert.Contains((await reply).Status, new[] { 200, 404 });
            Assert.False(await db.GetCollection<SupportReply>("SupportReplies").Find(r => r.TicketId == ticket.Id).AnyAsync());
            Assert.False(await db.GetCollection<SupportTicket>("SupportTickets").Find(r => r.Id == ticket.Id).AnyAsync());
            Assert.False(await db.GetCollection<Notification>("Notifications").Find(r => r.TargetId == ticket.Id).AnyAsync());
        }
    });
    [LocalMongoFact]
    public Task Reopening_cannot_bypass_the_open_ticket_cap() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new SupportPersistenceTests.Clock(); var settings = new SupportOptions { MaxOpenTickets = 1 };
        var store = new MongoSupportStore(f.Db, Options.Create(settings), clock); var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var first = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Value!;
        await store.ChangeStatusAsync(admin.Id, first.Id, new("in_progress", "Start", 1), true, default);
        await store.ChangeStatusAsync(admin.Id, first.Id, new("resolved", "Done", 2), true, default);
        Assert.Equal(200, (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Status);
        Assert.Equal(429, (await store.ChangeStatusAsync(f.User.Id, first.Id, new("open", "Reopen", 3), false, default)).Status);
        Assert.Equal("resolved", (await new SupportQueries(f.Db).GetMineAsync(f.User.Id, first.Id, 1, 20, default)).Value!.Status);
    });
    [LocalMongoFact]
    public Task Reader_detail_exposes_configured_reopening_deadline() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new SupportPersistenceTests.Clock(); var options = Options.Create(new SupportOptions { ReopenDays = 14 });
        var store = new MongoSupportStore(f.Db, options, clock); var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var ticket = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Value!;
        await store.ChangeStatusAsync(admin.Id, ticket.Id, new("in_progress", "Start", 1), true, default);
        await store.ChangeStatusAsync(admin.Id, ticket.Id, new("resolved", "Resolved", 2), true, default);
        var detail = (await new SupportQueries(f.Db, options).GetMineAsync(f.User.Id, ticket.Id, 1, 20, default)).Value!;
        Assert.Equal(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), detail.ReopenUntil);
    });
    [LocalMongoFact]
    public Task Transitions_assignment_and_reopening_enforce_role_category_and_version() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new SupportPersistenceTests.Clock(); var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), clock);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin"); var librarian = await SupportPersistenceTests.UserAsync(f.Db, "librarian");
        var ticket = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request("account"), default)).Value!;
        Assert.Equal(404, (await store.AddNoteAsync(librarian.Id, ticket.Id, new("No access", 1), default)).Status);
        Assert.Equal(404, (await store.AssignAsync(admin.Id, ticket.Id, new(librarian.Id, 1), default)).Status);
        Assert.Equal(409, (await store.ChangeStatusAsync(admin.Id, ticket.Id, new("closed", "Skip", 1), true, default)).Status);
        Assert.Equal(200, (await store.AssignAsync(admin.Id, ticket.Id, new(admin.Id, 1), default)).Status);
        Assert.Equal(200, (await store.ChangeStatusAsync(admin.Id, ticket.Id, new("in_progress", "Start", 2), true, default)).Status);
        Assert.Equal(200, (await store.ChangeStatusAsync(admin.Id, ticket.Id, new("resolved", "Done", 3), true, default)).Status);
        Assert.Equal(409, (await store.ReplyAsync(f.User.Id, ticket.Id, new("Need to reopen", 4), false, default)).Status);
        clock.Now = clock.Now.AddDays(7);
        Assert.Equal(200, (await store.ChangeStatusAsync(f.User.Id, ticket.Id, new("open", "Reopen", 4), false, default)).Status);
        await f.Db.Users.UpdateOneAsync(u => u.Id == admin.Id, Builders<User>.Update.Set(u => u.Role, "user"));
        Assert.Equal(403, (await store.AddNoteAsync(admin.Id, ticket.Id, new("Old role", 5), default)).Status);
    });

    [LocalMongoFact]
    public Task Messages_share_a_twenty_per_hour_budget_and_rollback_does_not_leave_notes() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new SupportPersistenceTests.Clock(); var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), clock);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var ticket = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Value!;
        for (var i = 0; i < 20; i++) Assert.Equal(200, (await (i % 2 == 0 ? store.AddNoteAsync(admin.Id, ticket.Id, new("Note", i + 1), default) : store.ReplyAsync(admin.Id, ticket.Id, new("Reply", i + 1), true, default))).Status);
        Assert.Equal(429, (await store.AddNoteAsync(admin.Id, ticket.Id, new("Too many", 21), default)).Status);
        Assert.Equal(10, await db.GetCollection<SupportInternalNote>("SupportInternalNotes").CountDocumentsAsync(FilterDefinition<SupportInternalNote>.Empty));
        Assert.Equal(21, (await new SupportQueries(f.Db).GetMineAsync(f.User.Id, ticket.Id, 1, 20, default)).Value!.Version);
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(200, (await store.ReplyAsync(admin.Id, ticket.Id, new("New window", 21), true, default)).Status);
    });

    [LocalMongoFact]
    public Task Private_references_invalid_inputs_and_suspended_intake_are_rejected() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new SupportPersistenceTests.Clock(); var settings = new SupportOptions(); var store = new MongoSupportStore(f.Db, Options.Create(settings), clock);
        var other = await SupportPersistenceTests.UserAsync(f.Db, "user");
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = other.Id, BookId = f.Book.Id };
        await f.Db.Loans.InsertOneAsync(loan);
        Assert.Equal(404, (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request() with { LoanId = loan.Id }, default)).Status);
        Assert.Equal(400, (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request() with { Subject = new string('x', 161) }, default)).Status);
        var request = SupportPersistenceTests.Request(); var ticket = (await store.CreateAsync(f.User.Id, request, default)).Value!;
        settings.AcceptNewTickets = false;
        Assert.Equal(409, (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Status);
        Assert.Equal(200, (await store.CreateAsync(f.User.Id, request, default)).Status);
        Assert.Equal(200, (await store.ReplyAsync(f.User.Id, ticket.Id, new("Existing stays usable", 1), false, default)).Status);
        var query = new SupportQueries(f.Db);
        Assert.Equal(400, (await query.ListMineAsync(f.User.Id, new(int.MaxValue, 100), default)).Status);
        Assert.Equal(400, (await query.GetMineAsync(f.User.Id, ticket.Id, 0, 20, default)).Status);
    });

    [LocalMongoFact]
    public Task Staff_owner_projection_never_contains_internal_notes() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), new SupportPersistenceTests.Clock());
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var ticket = (await store.CreateAsync(admin.Id, SupportPersistenceTests.Request(), default)).Value!;
        await store.AddNoteAsync(admin.Id, ticket.Id, new("Hidden even from own reader DTO", 1), default);
        var dto = (await new SupportQueries(f.Db).GetMineAsync(admin.Id, ticket.Id, 1, 20, default)).Value!;
        Assert.DoesNotContain("Hidden", System.Text.Json.JsonSerializer.Serialize(dto));
        Assert.Empty(dto.Replies.Items);
    });
}
