using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Controllers;

[ApiController, Route("api/branches"), Authorize]
public sealed class BranchesController(BranchStore store, MongoDBService mongo) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Public(CancellationToken ct) => Ok(await store.PublicAsync(ct));

    [HttpGet("context"), AllowAnonymous]
    public async Task<IActionResult> Context(CancellationToken ct) => Ok(new { enabled = await BranchRequestContext.EnabledAsync(mongo._database, ct) });

    [HttpGet("my")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        if (!ObjectId.TryParse(Actor, out _) || !await mongo.Users.Find(u => u.Id == Actor && u.IsActive && (u.Role == "admin" || u.Role == "librarian")).AnyAsync(ct))
            return ProblemCode("branch_forbidden", 403);
        return Ok(await store.MineAsync(Actor, ct));
    }

    [HttpPost, Authorize(Roles = "admin")]
    public async Task<IActionResult> Create(CreateBranchRequest request, CancellationToken ct) => Result(await store.CreateAsync(Actor, request, ct));

    [HttpPut("{id}"), Authorize(Roles = "admin")]
    public async Task<IActionResult> Update(string id, UpdateBranchRequest request, CancellationToken ct) => Result(await store.UpdateAsync(Actor, id, request, ct));

    [HttpPut("{id}/staff/{userId}"), Authorize(Roles = "admin")]
    public async Task<IActionResult> Assign(string id, string userId, AssignBranchRequest request, CancellationToken ct) => Result(await store.AssignAsync(Actor, id, userId, request, ct));

    [HttpGet("{id}/staff"), Authorize(Roles = "admin")]
    public async Task<IActionResult> Staff(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(Actor, out _) || !await mongo.Users.Find(u => u.Id == Actor && u.IsActive && u.Role == "admin").AnyAsync(ct)) return ProblemCode("branch_forbidden", 403);
        if (!ObjectId.TryParse(id, out _)) return BadRequest();
        var people = await mongo.Users.Find(u => u.Role == "librarian" && u.IsActive).SortBy(u => u.Username)
            .Project(u => new { u.Id, u.Username, u.DisplayName }).ToListAsync(ct);
        var assignments = await mongo._database.GetCollection<WebAppBookLibrary.Models.BranchStaffAssignment>("BranchStaffAssignments").Find(a => a.BranchId == id).ToListAsync(ct);
        return Ok(people.Select(u => new { user = u, assignment = assignments.SingleOrDefault(a => a.UserId == u.Id) }));
    }

    private IActionResult Result<T>(BranchResult<T> result) => result.Success ? Ok(result.Value) : ProblemCode(result.ErrorCode!, result.ErrorCode switch {
        "branch_forbidden" => 403, "branch_not_found" => 404, "branch_invalid" or "branch_staff_invalid" => 400, _ => 409
    });

    private static ObjectResult ProblemCode(string code, int status)
    {
        var problem = new ProblemDetails { Status = status, Title = code }; problem.Extensions["code"] = code;
        return new(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
