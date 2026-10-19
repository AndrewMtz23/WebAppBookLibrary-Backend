using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Support;

namespace WebAppBookLibrary.Tests;
public sealed class SupportRetentionTests
{
    [LocalMongoFact]
    public Task Purge_uses_calendar_anniversary_and_removes_all_ticket_dependents() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db);
        var clock = new SupportPersistenceTests.Clock();
        var options = Options.Create(new SupportOptions());
        var store = new MongoSupportStore(f.Db, options, clock);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var ticket = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Value!;
        await store.ReplyAsync(admin.Id, ticket.Id, new("Private answer", 1), true, default);
        await store.AddNoteAsync(admin.Id, ticket.Id, new("Private note", 2), default);
        var closed = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        await db.GetCollection<SupportTicket>("SupportTickets").UpdateOneAsync(t => t.Id == ticket.Id, Builders<SupportTicket>.Update.Set(t => t.Status, "closed").Set(t => t.ClosedAt, closed));
        var retention = new SupportRetention(f.Db, options);
        Assert.Equal(0, await retention.PurgeExpiredAsync(new DateTime(2025, 2, 28, 11, 59, 59, DateTimeKind.Utc), 100, default));
        Assert.Equal(1, await retention.PurgeExpiredAsync(new DateTime(2025, 2, 28, 12, 0, 0, DateTimeKind.Utc), 100, default));
        foreach (var collection in new[] { "SupportTickets", "SupportReplies", "SupportInternalNotes", "SupportHistory", "Notifications", "AccountMailOutbox" })
            Assert.Equal(0, await db.GetCollection<BsonDocument>(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var audit = await db.GetCollection<BsonDocument>("SupportPurgeEvents").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(new[] { "Count", "CreatedAt", "Type", "_id" }, audit.Names.Order(StringComparer.Ordinal));
    });

    [LocalMongoFact]
    public Task Account_deletion_purges_owned_tickets_and_anonymizes_staff_contributions() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var staff = await SupportPersistenceTests.UserAsync(f.Db, "librarian");
        var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), new SupportPersistenceTests.Clock());
        var readerTicket = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Value!;
        var staffTicket = (await store.CreateAsync(staff.Id, SupportPersistenceTests.Request(), default)).Value!;
        await store.ReplyAsync(staff.Id, readerTicket.Id, new("Answer remains", 1), true, default);
        await store.AssignAsync(admin.Id, readerTicket.Id, new(staff.Id, 2), default);
        var users = new MongoAdminUserStore(f.Db);
        await users.SetStatusSafelyAsync(admin.Id, staff.Id, false, DateTime.UtcNow, default);
        Assert.Equal(AdminStoreMutationOutcome.Success, (await users.DeletePermanentlyAsync(admin.Id, staff.Id, default)).Outcome);
        Assert.False(await db.GetCollection<SupportTicket>("SupportTickets").Find(t => t.Id == staffTicket.Id).AnyAsync());
        var reply = await db.GetCollection<SupportReply>("SupportReplies").Find(r => r.TicketId == readerTicket.Id).SingleAsync();
        Assert.Null(reply.AuthorId); Assert.Equal("Answer remains", reply.Text);
        Assert.Null((await db.GetCollection<SupportTicket>("SupportTickets").Find(t => t.Id == readerTicket.Id).SingleAsync()).AssigneeId);
    });
}
