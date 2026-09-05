using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Contracts.Dashboard;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController(DashboardService service) : ControllerBase
{
    [HttpGet("reader")]
    [Authorize(Policy = PolicyNames.BorrowBooks)]
    public async Task<IActionResult> Reader([FromQuery] DashboardQuery query, CancellationToken token)
    {
        if (!TryPeriod(query, out var period, out var error)) return DashboardProblem(error);
        var result = await service.ReaderAsync(User.Identity?.Name ?? string.Empty, period!, token);
        return result is null ? ApiProblemFactory.Result(403, "Dashboard is not permitted") : Ok(result);
    }

    [HttpGet("librarian")]
    [Authorize(Policy = PolicyNames.ViewAllLoans)]
    public async Task<IActionResult> Librarian([FromQuery] DashboardQuery query, CancellationToken token)
    {
        if (!TryPeriod(query, out var period, out var error)) return DashboardProblem(error);
        return Ok(await service.LibrarianAsync(period!, token));
    }

    [HttpGet("admin")]
    [Authorize(Policy = PolicyNames.ManageUsers)]
    public async Task<IActionResult> Admin([FromQuery] DashboardQuery query, CancellationToken token)
    {
        if (!TryPeriod(query, out var period, out var error)) return DashboardProblem(error);
        return Ok(await service.AdminAsync(period!, token));
    }

    private static bool TryPeriod(DashboardQuery query, out DashboardPeriod? period, out string error) => DashboardPeriod.TryCreate(query.From, query.To, query.Timezone, out period, out error);
    private static ObjectResult DashboardProblem(string code)
    {
        var result = ApiProblemFactory.Result(400, "Invalid dashboard period");
        if (result.Value is ProblemDetails details) details.Extensions["code"] = code;
        return result;
    }
}
