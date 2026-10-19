using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Contracts.Support;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services.Support;
namespace WebAppBookLibrary.Controllers;

[ApiController, Route("api/support/tickets"), Authorize(Policy = PolicyNames.ManageSupport)]
public sealed class StaffSupportController(MongoSupportStore store, SupportQueries queries) : SupportApiController
{
    [HttpGet] public async Task<IActionResult> List([FromQuery] SupportQuery query, CancellationToken ct) => Result(await queries.ListStaffAsync(ActorId, query, ct));
    [HttpGet("assignees")] public async Task<IActionResult> Assignees(string category, CancellationToken ct) => Result(await queries.AssigneesAsync(ActorId, category, ct));
    [HttpGet("{id}/staff")] public async Task<IActionResult> Detail(string id, int page = 1, int pageSize = 20, CancellationToken ct = default) => Result(await queries.GetStaffAsync(ActorId, id, page, pageSize, ct));
    [HttpPost("{id}/staff/replies")] public async Task<IActionResult> Reply(string id, AddSupportMessage request, CancellationToken ct) => Result(await store.ReplyAsync(ActorId, id, request, true, ct));
    [HttpPost("{id}/notes")] public async Task<IActionResult> Note(string id, AddSupportMessage request, CancellationToken ct) => Result(await store.AddNoteAsync(ActorId, id, request, ct));
    [HttpPut("{id}/staff/status")] public async Task<IActionResult> Status(string id, ChangeSupportStatus request, CancellationToken ct) => Result(await store.ChangeStatusAsync(ActorId, id, request, true, ct));
    [HttpPut("{id}/assignment")] public async Task<IActionResult> Assign(string id, AssignSupportTicket request, CancellationToken ct) => Result(await store.AssignAsync(ActorId, id, request, ct));
}
