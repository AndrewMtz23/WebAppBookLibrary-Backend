using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WebAppBookLibrary.Services.Branches;

public sealed class BranchApiFilter : IAsyncExceptionFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is not BranchAccessException ex) return Task.CompletedTask;
        var status = ex.Code switch { "branch_forbidden" => 403, "branch_required" => 400, "branch_unavailable" => 404, _ => 409 };
        var problem = new ProblemDetails { Status = status, Title = "No se pudo operar en esta sucursal." }; problem.Extensions["code"] = ex.Code;
        context.Result = new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}
