using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace WebAppBookLibrary.Errors;

public static class ApiProblemFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ObjectResult Result(int statusCode, string title)
    {
        var result = new ObjectResult(Create(statusCode, title))
        {
            StatusCode = statusCode
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static ProblemDetails Create(int statusCode, string title)
    {
        return new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = $"https://httpstatuses.com/{statusCode}"
        };
    }

    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string title,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            Create(statusCode, title),
            JsonOptions,
            cancellationToken);
    }
}
