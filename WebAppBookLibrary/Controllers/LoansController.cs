using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LoansController : ControllerBase
{
    private readonly LoanService _loanService;
    private readonly Logservice? _logService;

    public LoansController(LoanService loanService)
        : this(loanService, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public LoansController(LoanService loanService, Logservice? logService)
    {
        _loanService = loanService;
        _logService = logService;
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BorrowBooks)]
    public async Task<IActionResult> CreateLoan([FromBody] CreateLoanRequest request, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(request.BookId, out _))
            return LoanProblem(400, "Invalid book identifier", "invalid_identifier");

        var username = User.Identity?.Name ?? string.Empty;
        var result = await _loanService.ReserveAsync(request.BookId, username, username, DateTime.UtcNow, cancellationToken);

        if (!result.Success)
        {
            return result.ErrorCode switch
            {
                LoanOperationErrorCodes.BookUnavailable =>
                    LoanProblem(409, "Book is not available", result.ErrorCode),
                LoanOperationErrorCodes.DuplicateActive =>
                    LoanProblem(409, "An active reservation already exists", result.ErrorCode),
                LoanOperationErrorCodes.BookNotFound =>
                    LoanProblem(404, "Book not found", result.ErrorCode),
                LoanOperationErrorCodes.InvalidUser =>
                    LoanProblem(403, "Loan is not permitted", result.ErrorCode),
                _ => LoanProblem(500, "Loan could not be created", result.ErrorCode)
            };
        }

        if (_logService is not null) await _logService.LoanChangedAsync("created", username, result.Loan!.Id, new Dictionary<string, string> { ["mediaType"] = result.Loan.MediaType });
        return StatusCode(StatusCodes.Status201Created, new
        {
            message = "Loan created successfully",
            data = LoanResponse.From(result.Loan!, DateTime.UtcNow)
        });
    }

    [HttpGet("my")]
    [Authorize(Policy = PolicyNames.BorrowBooks)]
    public async Task<IActionResult> GetMyLoans([FromQuery(Name = "")] LoanQuery query, CancellationToken token)
    {
        var username = User.Identity?.Name ?? string.Empty;
        var loans = await _loanService.SearchMineAsync(username, query, token);
        return loans is null ? LoanProblem(403, "Loans are not permitted", LoanOperationErrorCodes.InvalidUser) : Ok(loans);
    }

    [HttpGet]
    [Authorize(Policy = PolicyNames.ViewAllLoans)]
    public async Task<IActionResult> GetAllLoans([FromQuery(Name = "")] LoanQuery query, CancellationToken token)
    {
        return Ok(await _loanService.SearchAsync(query, token));
    }

    [HttpPut("{id}/return")]
    public async Task<IActionResult> ReturnLoan(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _))
            return LoanProblem(400, "Invalid loan identifier", "invalid_identifier");

        var username = User.Identity?.Name ?? string.Empty;
        var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        var result = await _loanService.ReturnReservationAsync(id, username, callerRole, DateTime.UtcNow, cancellationToken);

        if (!result.Success)
        {
            return result.ErrorCode switch
            {
                LoanOperationErrorCodes.Forbidden or LoanOperationErrorCodes.InvalidUser =>
                    LoanProblem(403, "Loan return is not permitted", result.ErrorCode),
                LoanOperationErrorCodes.LoanNotFound =>
                    LoanProblem(404, "Loan not found", result.ErrorCode),
                LoanOperationErrorCodes.InvalidTransition =>
                    LoanProblem(409, "Loan cannot transition to returned", result.ErrorCode),
                _ => LoanProblem(500, "Loan could not be returned", result.ErrorCode)
            };
        }

        var message = result.Idempotent ? "Loan was already returned" : "Loan marked as returned";
        if (!result.Idempotent && _logService is not null) await _logService.LoanChangedAsync("returned", username, id);
        return Ok(new { message, idempotent = result.Idempotent });
    }

    [HttpPut("{id}/cancel")]
    public async Task<IActionResult> CancelLoan(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _))
            return LoanProblem(400, "Invalid loan identifier", "invalid_identifier");

        var username = User.Identity?.Name ?? string.Empty;
        var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        var result = await _loanService.CancelReservationAsync(id, username, callerRole, DateTime.UtcNow, cancellationToken);
        if (!result.Success)
        {
            return result.ErrorCode switch
            {
                LoanOperationErrorCodes.Forbidden or LoanOperationErrorCodes.InvalidUser => LoanProblem(403, "Loan cancellation is not permitted", result.ErrorCode),
                LoanOperationErrorCodes.LoanNotFound => LoanProblem(404, "Loan not found", result.ErrorCode),
                LoanOperationErrorCodes.InvalidTransition => LoanProblem(409, "Loan cannot transition to cancelled", result.ErrorCode),
                _ => LoanProblem(500, "Loan could not be cancelled", result.ErrorCode)
            };
        }
        if (!result.Idempotent && _logService is not null) await _logService.LoanChangedAsync("cancelled", username, id);
        return Ok(new { message = result.Idempotent ? "Loan was already cancelled" : "Loan cancelled", idempotent = result.Idempotent });
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = PolicyNames.DeleteBooks)]
    public async Task<IActionResult> DeleteLoan(string id)
    {
        if (!ObjectId.TryParse(id, out _))
            return LoanProblem(400, "Invalid loan identifier", "invalid_identifier");

        var result = await _loanService.DeleteLoanAsync(id);
        if (!result.Success)
            return ApiProblemFactory.Result(404, "Loan could not be deleted");

        return Ok(new { message = result.Message });
    }

    private static ObjectResult LoanProblem(int statusCode, string title, string errorCode)
    {
        var result = ApiProblemFactory.Result(statusCode, title);
        if (result.Value is ProblemDetails problem)
            problem.Extensions["code"] = errorCode;

        return result;
    }
}
