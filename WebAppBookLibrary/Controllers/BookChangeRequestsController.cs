using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Books;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Controllers;

public sealed record BookChangeDecision(bool Approve, long ExpectedVersion);

[ApiController, Authorize(Roles = "admin,librarian"), Route("api/book-change-requests")]
public sealed class BookChangeRequestsController(BookChangeService service, MongoDBService mongo, BranchRequestContext context) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpPost("books/{bookId}")]
    public async Task<IActionResult> Propose(string bookId, BookWriteRequest request, CancellationToken ct)
    {
        var branch = await BranchRequestContext.ResolveAsync(mongo, context, ct);
        if (branch is null) return Error("branch_migration_required", 409);
        return Result(await service.ProposeAsync(Actor, branch, bookId, request, request.ExpectedMetadataVersion ?? 0, ct));
    }

    [HttpPut("{id}/decision"), Authorize(Roles = "admin")]
    public async Task<IActionResult> Decide(string id, BookChangeDecision request, CancellationToken ct) => Result(await service.DecideAsync(Actor, id, request.Approve, request.ExpectedVersion, ct));

    [HttpGet]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page is < 1 or > 1000000 || pageSize is < 1 or > 100) return Error("invalid_page", 400);
        var user = await mongo.Users.Find(u => u.Id == Actor && u.IsActive && (u.Role == "admin" || u.Role == "librarian")).FirstOrDefaultAsync(ct);
        if (user is null) return Error("branch_forbidden", 403);
        var filter = Builders<BookChangeRequest>.Filter.Empty;
        if (user.Role != "admin")
        {
            var branch = await BranchRequestContext.ResolveAsync(mongo, context, ct);
            if (branch is null) return Error("branch_migration_required", 409);
            filter &= Builders<BookChangeRequest>.Filter.Eq(r => r.BranchId, branch) & Builders<BookChangeRequest>.Filter.Eq(r => r.RequestedBy, Actor);
        }
        var requests = mongo._database.GetCollection<BookChangeRequest>("BookChangeRequests");
        var totalCount = await requests.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await requests.Find(filter).SortByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);
        return Ok(new { items, totalCount });
    }

    private IActionResult Result<T>(BranchResult<T> result) => result.Success ? Ok(result.Value) : Error(result.ErrorCode!, result.ErrorCode == "branch_forbidden" ? 403 : result.ErrorCode!.EndsWith("not_found") ? 404 : 409);
    private static ObjectResult Error(string code, int status)
    {
        var p = new ProblemDetails { Title = code, Status = status }; p.Extensions["code"] = code;
        return new(p) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
