using System.Net;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using WebAppBookLibrary.Contracts.Audit;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public class AuditLogTests
{
    [Fact]
    public void Persisted_entry_keeps_exception_type_but_not_message_or_stack()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.RouteValues["controller"] = "Books";
        context.Request.RouteValues["action"] = "Create";
        var exception = CreateSensitiveException();

        var entry = AuditLogEntryFactory.Create(
            "ERROR",
            $"Book creation failed: {exception}",
            exception,
            context);
        entry.Id = ObjectId.GenerateNewId().ToString();
        var persistedDocument = entry.ToBsonDocument().ToJson();

        Assert.Equal(nameof(InvalidOperationException), entry.Exception);
        Assert.Equal("Book creation failed: [redacted]", entry.Message);
        Assert.DoesNotContain("secret-password-value", persistedDocument);
        Assert.NotNull(exception.StackTrace);
        Assert.DoesNotContain(exception.StackTrace, persistedDocument);
        Assert.Equal(HttpMethods.Post, entry.Method);
        Assert.Equal("192.0.2.10", entry.IP);
    }

    [Fact]
    public void Public_audit_response_does_not_expose_exception_field()
    {
        var entry = new LogEntry
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Timestamp = DateTime.UtcNow,
            Level = "ERROR",
            Message = "Book creation failed",
            Exception = nameof(InvalidOperationException)
        };

        var response = AuditLogResponse.From(entry);
        var propertyNames = response.GetType().GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("Exception", propertyNames);
        Assert.DoesNotContain("ExceptionType", propertyNames);
        Assert.Equal(entry.Id, response.Id);
    }

    [Fact]
    public void Public_audit_response_redacts_legacy_exception_message()
    {
        var entry = new LogEntry
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Level = "ERROR",
            Message = "Error creating book: secret-password-value",
            Exception = "System.InvalidOperationException: secret-password-value\n   at Legacy.Code()"
        };

        var response = AuditLogResponse.From(entry);

        Assert.Equal("Error creating book: [redacted]", response.Message);
        Assert.DoesNotContain("secret-password-value", response.Message);
    }

    private static InvalidOperationException CreateSensitiveException()
    {
        try
        {
            throw new InvalidOperationException("secret-password-value");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}
