using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public LoansController(LoanService loanService)
    {
        _loanService = loanService;
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BorrowBooks)]
    public async Task<IActionResult> CreateLoan([FromBody] CreateLoanRequest request)
    {
        if (!ObjectId.TryParse(request.BookId, out _))
            return LoanProblem(400, "Invalid book identifier", "invalid_identifier");

        var username = User.Identity?.Name ?? string.Empty;
        var result = await _loanService.CreateLoanAsync(request.BookId, username);

        if (!result.Success)
        {
            return result.ErrorCode switch
            {
                LoanOperationErrorCodes.BookUnavailable =>
                    LoanProblem(409, "Book is not available", result.ErrorCode),
                LoanOperationErrorCodes.InvalidUser =>
                    LoanProblem(403, "Loan is not permitted", result.ErrorCode),
                _ => LoanProblem(500, "Loan could not be created", result.ErrorCode)
            };
        }

        return Ok(new
        {
            message = "Loan created successfully",
            data = result.Loan
        });
    }

    [HttpGet("my")]
    [Authorize(Policy = PolicyNames.BorrowBooks)]
    public async Task<IActionResult> GetMyLoans()
    {
        var username = User.Identity?.Name ?? string.Empty;
        var loans = await _loanService.GetLoansByUsernameAsync(username);

        return Ok(new { message = "My loans retrieved", data = loans });
    }

    [HttpGet]
    [Authorize(Policy = PolicyNames.ViewAllLoans)]
    public async Task<IActionResult> GetAllLoans()
    {
        var loans = await _loanService.GetAllLoansWithDetailsAsync();
        return Ok(new { message = "All loans retrieved", data = loans });
    }

    [HttpPut("{id}/return")]
    public async Task<IActionResult> ReturnLoan(string id)
    {
        if (!ObjectId.TryParse(id, out _))
            return LoanProblem(400, "Invalid loan identifier", "invalid_identifier");

        var username = User.Identity?.Name ?? string.Empty;
        var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        var result = await _loanService.MarkAsReturnedAsync(id, username, callerRole);

        if (!result.Success)
        {
            return result.ErrorCode switch
            {
                LoanOperationErrorCodes.Forbidden or LoanOperationErrorCodes.InvalidUser =>
                    LoanProblem(403, "Loan return is not permitted", result.ErrorCode),
                LoanOperationErrorCodes.LoanNotFound =>
                    LoanProblem(404, "Loan not found", result.ErrorCode),
                _ => LoanProblem(500, "Loan could not be returned", result.ErrorCode)
            };
        }

        var message = result.Idempotent ? "Loan was already returned" : "Loan marked as returned";
        return Ok(new { message, idempotent = result.Idempotent });
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
