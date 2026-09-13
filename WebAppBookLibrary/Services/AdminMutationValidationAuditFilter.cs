using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using MongoDB.Bson;

namespace WebAppBookLibrary.Services;

public sealed class AdminMutationValidationAuditFilter(IServiceProvider services) : IAsyncAlwaysRunResultFilter, IOrderedFilter
{
    public int Order => -3000;

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var action = context.HttpContext.Request.Path.Value?.EndsWith("/role", StringComparison.OrdinalIgnoreCase) == true ? "SetRole" :
            context.HttpContext.Request.Path.Value?.EndsWith("/status", StringComparison.OrdinalIgnoreCase) == true ? "SetStatus" : null;
        if (!context.ModelState.IsValid && context.HttpContext.Request.Method == HttpMethods.Put && action is not null)
        {
            var actorId = Canonical(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)) ?? "unknown";
            var rawTarget = context.RouteData.Values["id"]?.ToString();
            var targetId = Canonical(rawTarget) ?? "invalid";
            var auditAction = action == "SetRole" ? "role_change_attempt" : "status_change_attempt";
            try
            {
                var audit = services.GetService<IAdminUserAudit>();
                if (audit is not null)
                    await audit.UserChangedAsync(auditAction, actorId, targetId, new Dictionary<string, string>
                    {
                        ["result"] = "failed",
                        ["reasonCode"] = "invalid_request"
                    });
            }
            catch
            {
                // Preserve the framework's 400 response when the independent audit write is unavailable.
            }
        }

        await next();
    }

    private static string? Canonical(string? value) => ObjectId.TryParse(value, out var objectId) ? objectId.ToString() : null;
}
