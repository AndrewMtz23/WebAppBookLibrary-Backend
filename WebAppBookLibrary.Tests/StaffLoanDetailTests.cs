using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Security;
using Moq;

namespace WebAppBookLibrary.Tests;
public sealed class StaffLoanDetailTests
{
    [Theory]
    [InlineData(RoleNames.User, "000000000000000000000001", HttpStatusCode.Forbidden)]
    [InlineData(RoleNames.Admin, "invalid", HttpStatusCode.BadRequest)]
    [InlineData(RoleNames.Librarian, "000000000000000000000001", HttpStatusCode.NotFound)]
    public async Task Detail_enforces_policy_and_identifier(string role, string id, HttpStatusCode expected)
    {
        var store = new Mock<ILoanStore>();
        await using var app = await StaffHttpQueryTests.StartApp(s => s.AddTransient(_ => new LoansController(new LoanService(store.Object))), role);
        using var client = StaffHttpQueryTests.Client(app);
        Assert.Equal(expected, (await client.GetAsync("/api/loans/" + id)).StatusCode);
    }

    [LocalMongoFact]
    public Task Detail_has_safe_names_bounded_targeted_events_and_honest_fallback() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Circulación", DigitalResourceUrl = "https://secret.invalid" };
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "ana", DisplayName = "Ana", Email = "private@example.org", PasswordHash = "secret-hash" };
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id, UserId = user.Id, LoanDate = DateTime.UtcNow.AddDays(-10), ReturnedAt = DateTime.UtcNow, Status = "returned", Notes = "private-note" };
        await db.GetCollection<Book>("Books").InsertOneAsync(book);
        await db.GetCollection<User>("Users").InsertOneAsync(user);
        await db.GetCollection<Loan>("Loans").InsertOneAsync(loan);
        var logs = Enumerable.Range(0, 55).Select(i => new LogEntry { Id = ObjectId.GenerateNewId().ToString(), TargetId = loan.Id, TargetType = "loan", EventType = "loan.returned", Timestamp = DateTime.UtcNow.AddMinutes(-i), ActorUsername = "staff", IP = "secret-ip", Message = "secret-message" }).ToList();
        logs.Add(new LogEntry { TargetId = loan.Id, TargetType = "user", EventType = "loan.created", Message = "wrong-target" });
        logs.Add(new LogEntry { TargetId = loan.Id, TargetType = "loan", EventType = "loan.secret", Message = "wrong-event" });
        await db.GetCollection<LogEntry>("LogEntries").InsertManyAsync(logs);
        var store = new MongoLoanStore(db.GetCollection<Book>("Books"), db.GetCollection<Loan>("Loans"), db.GetCollection<User>("Users"));
        await using var app = await StaffHttpQueryTests.StartApp(s => s.AddTransient(_ => new LoansController(new LoanService(store))), RoleNames.Librarian);
        using var client = StaffHttpQueryTests.Client(app);
        var response = await client.GetAsync("/api/loans/" + loan.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        foreach (var secret in new[] { "secret", "private", "wrong-target", "wrong-event", "password", "digitalResourceUrl" }) Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Circulación", doc.RootElement.GetProperty("loan").GetProperty("bookTitle").GetString());
        Assert.Equal("Ana", doc.RootElement.GetProperty("loan").GetProperty("displayName").GetString());
        Assert.True(doc.RootElement.GetProperty("historyTruncated").GetBoolean());
        var history = doc.RootElement.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(51, history.Length);
        Assert.Single(history, e => e.GetProperty("eventType").GetString() == "created" && e.GetProperty("source").GetString() == "recorded-date");
        Assert.Equal(50, history.Count(e => e.GetProperty("source").GetString() == "audit"));
        // ObjectId lookup is case-insensitive; the same resource must retain its audit history.
        var uppercaseResponse = await client.GetAsync("/api/loans/" + loan.Id.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.OK, uppercaseResponse.StatusCode);
        using var uppercaseDoc = JsonDocument.Parse(await uppercaseResponse.Content.ReadAsStringAsync());
        Assert.Equal(50, uppercaseDoc.RootElement.GetProperty("history").EnumerateArray()
            .Count(e => e.GetProperty("source").GetString() == "audit"));
        Assert.True(uppercaseDoc.RootElement.GetProperty("historyTruncated").GetBoolean());
        await db.GetCollection<LogEntry>("LogEntries").InsertOneAsync(new LogEntry
        {
            TargetId = loan.Id.ToUpperInvariant(), TargetType = "loan", EventType = "loan.cancelled",
            Timestamp = DateTime.UtcNow.AddMinutes(1), ActorUsername = "second-staff"
        });
        var mixedCaseHistory = await new LoanService(store).GetDetailAsync(loan.Id, default);
        Assert.Contains(mixedCaseHistory!.History, e => e.Source == "audit" && e.ActorUsername == "second-staff");
    });
}
