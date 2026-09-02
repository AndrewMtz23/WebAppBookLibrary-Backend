using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Errors;

namespace WebAppBookLibrary.Tests;

public class ApiProblemTests
{
    [Theory]
    [InlineData(400, "Invalid request")]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "Resource not found")]
    [InlineData(409, "Conflict")]
    [InlineData(429, "Too many requests")]
    [InlineData(500, "Internal server error")]
    public void Result_uses_safe_problem_details_shape(int status, string title)
    {
        var result = ApiProblemFactory.Result(status, title);

        Assert.Equal(status, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(status, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Null(problem.Detail);
        Assert.NotNull(problem.Type);
    }

    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(429, "Too many requests")]
    public async Task Writer_emits_problem_json_with_requested_status(int status, string title)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await ApiProblemFactory.WriteAsync(context, status, title);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        var payload = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(status, payload.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(title, payload.RootElement.GetProperty("title").GetString());
        Assert.False(payload.RootElement.TryGetProperty("detail", out _));
    }
}
