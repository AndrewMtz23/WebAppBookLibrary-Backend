using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Support;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;
public sealed class SupportNotificationTests
{
    [LocalMongoFact]
    public Task Mail_does_not_deliver_a_notice_for_a_missing_ticket() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db);
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.User.Id, Builders<User>.Update.Set(u => u.EmailVerifiedAt, DateTime.UtcNow));
        await db.GetCollection<NotificationPreferences>("NotificationPreferences").InsertOneAsync(new() { Id = f.User.Id, Email = true });
        var notice = new Notification { UserId = f.User.Id, TargetType = "support_ticket", TargetId = MongoDB.Bson.ObjectId.GenerateNewId().ToString(), Type = "support_update" };
        await db.GetCollection<Notification>("Notifications").InsertOneAsync(notice);
        Assert.Null(await NotificationMail.PrepareAsync(f.Db, notice.Id, DateTime.UtcNow, "https://example.invalid", new NotificationOptions(), default));
    });
    [LocalMongoFact]
    public Task Only_public_staff_actions_notify_owner_without_private_content() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db);
        var store = new MongoSupportStore(f.Db, Options.Create(new SupportOptions()), new SupportPersistenceTests.Clock());
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var ticket = (await store.CreateAsync(f.User.Id, SupportPersistenceTests.Request(), default)).Value!;
        Assert.Equal(200, (await store.AddNoteAsync(admin.Id, ticket.Id, new("secret note", 1), default)).Status);
        Assert.Equal(0, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(FilterDefinition<Notification>.Empty));
        Assert.Equal(200, (await store.ReplyAsync(admin.Id, ticket.Id, new("private staff reply", 2), true, default)).Status);
        var notice = await db.GetCollection<Notification>("Notifications").Find(FilterDefinition<Notification>.Empty).SingleAsync();
        Assert.Equal(f.User.Id, notice.UserId); Assert.Equal("support_ticket", notice.TargetType); Assert.Equal(ticket.Id, notice.TargetId);
        Assert.DoesNotContain("private", notice.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", notice.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(409, (await store.ReplyAsync(admin.Id, ticket.Id, new("duplicate", 2), true, default)).Status);
        Assert.Equal(1, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(FilterDefinition<Notification>.Empty));
    });
}
