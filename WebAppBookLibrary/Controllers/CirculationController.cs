using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Circulation;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController, Authorize]
public sealed class CirculationController(ICirculationStore store, MongoDBService db) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private bool Staff => User.IsInRole("admin") || User.IsInRole("librarian");
    private Task<bool> Active(CancellationToken ct) => db.Users.Find(u => u.Id == Actor && u.IsActive).AnyAsync(ct);
    private static bool Id(string? id) => ObjectId.TryParse(id, out _);
    private static bool Page(int page, int size, string? search = null) => page is >= 1 and <= 1000000 && size is >= 1 and <= 100 && (search?.Length ?? 0) <= 200;
    private ObjectResult Error(string code, int? status = null)
    {
        if (status is null or 409) CirculationTelemetry.Conflicts.Add(1);
        var result = ApiProblemFactory.Result(status ?? (code.EndsWith("not_found") ? 404 : code == "invalid_user" ? 403 : 409), "No se pudo completar la operación de circulación.");
        if (result.Value is ProblemDetails problem) problem.Extensions["code"] = code;
        return result;
    }

    [HttpGet("api/circulation/policy"), AllowAnonymous]
    public async Task<IActionResult> Policy(CancellationToken ct) => Ok(new CirculationPolicyResponse(await store.GetModeAsync(ct), store.Options.PickupHours, store.Options.LoanDays, store.Options.RenewalDays, store.Options.MaxRenewals));

    [HttpGet("api/circulation/my/books/{id}")]
    public async Task<IActionResult> MyBook(string id, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(id)) return Error("invalid_identifier", 400);
        var pickup = await db.PickupReservations.Find(p => p.BookId == id && p.UserId == Actor && p.Status == "ready")
            .Project(p => new { p.Id, p.Status, p.PickupExpiresAt }).FirstOrDefaultAsync(ct);
        var entry = await db.WaitlistEntries.Find(w => w.BookId == id && w.UserId == Actor && w.Status == "queued").FirstOrDefaultAsync(ct);
        return Ok(new { pickup, waiting = entry == null ? null : new { entry.Id, entry.Status, queuePosition = await store.GetQueuePositionAsync(id, entry.CreatedAt, entry.Id, ct) } });
    }

    [HttpGet("api/circulation/history/{id}")]
    public async Task<IActionResult> History(string id, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(id)) return Error("invalid_identifier", 400);
        if (!await db.CirculationHistory.Find(h => h.EntityId == id && (Staff || h.UserId == Actor)).AnyAsync(ct)) return Error("history_not_found", 404);
        return Ok(await store.GetHistoryAsync(id, ct));
    }

    [HttpGet("api/circulation/summary"), Authorize(Roles = "admin,librarian")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        using var session = await db._database.Client.StartSessionAsync(cancellationToken: ct);
        var summary = await session.WithTransactionAsync(async (tx, token) => new {
            ready = await db.PickupReservations.CountDocumentsAsync(tx, p => p.Status == "ready", cancellationToken: token),
            queued = await db.WaitlistEntries.CountDocumentsAsync(tx, w => w.Status == "queued", cancellationToken: token),
            loaned = await db.Loans.CountDocumentsAsync(tx, l => l.PolicyVersion == "circulation-v1" && (l.Status == "active" || l.Status == "overdue"), cancellationToken: token),
            pendingRenewals = await db.RenewalRequests.CountDocumentsAsync(tx, r => r.Status == "pending", cancellationToken: token)
        }, cancellationToken: ct);
        return Ok(summary);
    }

    [HttpPost("api/waitlist"), Authorize(Roles = "user")]
    public async Task<IActionResult> Join(JoinWaitlistRequest request, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(request.BookId) || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100) return Error("invalid_request", 400);
        var result = await store.JoinWaitlistAsync(Actor, request.BookId, ct, request.IdempotencyKey);
        return result.Success ? StatusCode(201, result) : Error(result.ErrorCode!);
    }
    [HttpGet("api/waitlist/my")]
    public async Task<IActionResult> Waitlist(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        return !Page(page, pageSize) ? Error("invalid_query", 400) : Ok(await store.GetMyWaitlistAsync(Actor, page, pageSize, ct));
    }
    [HttpDelete("api/waitlist/{id}")]
    public async Task<IActionResult> Leave(string id, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(id)) return Error("invalid_identifier", 400);
        return await store.LeaveWaitlistAsync(Actor, id, "Cancelada por el lector", ct) ? NoContent() : Error("waitlist_not_found", 404);
    }
    [HttpPost("api/pickup-reservations"), Authorize(Roles = "user")]
    public async Task<IActionResult> Reserve(ReservePickupRequest request, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(request.BookId) || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100) return Error("invalid_request", 400);
        var result = await store.ReserveForPickupAsync(Actor, request.BookId, request.IdempotencyKey, ct);
        return result.Success ? StatusCode(result.Idempotent ? 200 : 201, result.Reservation) : Error(result.ErrorCode!);
    }
    [HttpGet("api/pickup-reservations/my")]
    public async Task<IActionResult> MyPickups(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        return !Page(page, pageSize) ? Error("invalid_query", 400) : Ok(await store.GetMyPickupReservationsAsync(Actor, page, pageSize, ct));
    }
    [HttpGet("api/pickup-reservations"), Authorize(Roles = "admin,librarian")]
    public async Task<IActionResult> Pickups(string? status = null, string? search = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Page(page, pageSize, search) || status is not (null or "ready" or "collected" or "cancelled" or "expired")) return Error("invalid_query", 400);
        return Ok(await store.GetStaffPickupReservationsAsync(status, search, page, pageSize, ct));
    }
    [HttpPut("api/pickup-reservations/{id}/collect"), Authorize(Roles = "admin,librarian")]
    public async Task<IActionResult> Collect(string id, CollectPickupRequest request, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(id) || request.ExpectedVersion < 1) return Error("invalid_request", 400);
        var result = await store.CollectPickupReservationAsync(id, Actor, request.ExpectedVersion, ct);
        return result.Success ? Ok(new { loanId = result.Loan!.Id, result.Idempotent }) : Error(result.ErrorCode!);
    }
    [HttpPut("api/pickup-reservations/{id}/cancel")]
    public async Task<IActionResult> Cancel(string id, CancelPickupRequest request, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(id) || request.ExpectedVersion < 1 || request.Reason?.Length > 500) return Error("invalid_request", 400);
        if (!await db.PickupReservations.Find(p => p.Id == id && (Staff || p.UserId == Actor)).AnyAsync(ct)) return Error("pickup_not_found", 404);
        return await store.CancelPickupReservationAsync(id, Actor, Staff, request.Reason, request.ExpectedVersion, ct) ? NoContent() : Error("version_conflict");
    }
    [HttpPost("api/loans/{id}/renewal-requests"), Authorize(Roles = "user")]
    public async Task<IActionResult> RequestRenewal(string id, CreateRenewalRequest request, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(id) || request.Reason?.Length > 500 || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100) return Error("invalid_request", 400);
        var result = await store.RequestRenewalAsync(id, Actor, request.Reason, ct, request.IdempotencyKey);
        return result.Success ? StatusCode(201, result.Request) : Error(result.ErrorCode!);
    }
    [HttpGet("api/renewal-requests/my")]
    public async Task<IActionResult> MyRenewals(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        return !Page(page, pageSize) ? Error("invalid_query", 400) : Ok(await store.GetMyRenewalRequestsAsync(Actor, page, pageSize, ct));
    }
    [HttpGet("api/renewal-requests"), Authorize(Roles = "admin,librarian")]
    public async Task<IActionResult> Renewals(string? status = null, string? search = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Page(page, pageSize, search) || status is not (null or "pending" or "approved" or "rejected" or "cancelled")) return Error("invalid_query", 400);
        return Ok(await store.GetStaffRenewalRequestsAsync(status, search, page, pageSize, ct));
    }
    [HttpPut("api/renewal-requests/{id}/decision"), Authorize(Roles = "admin,librarian")]
    public async Task<IActionResult> Decide(string id, DecideRenewalRequest request, CancellationToken ct)
    {
        if (!await Active(ct)) return Error("invalid_user", 401);
        if (!Id(id) || request.ExpectedVersion < 1 || request.DecisionReason?.Length > 500 || (!request.Approve && string.IsNullOrWhiteSpace(request.DecisionReason))) return Error("invalid_request", 400);
        var result = await store.DecideRenewalAsync(id, Actor, request.Approve, request.DecisionReason, request.ExpectedVersion, ct);
        return result.Success ? Ok(result.Request) : Error(result.ErrorCode!);
    }
}
