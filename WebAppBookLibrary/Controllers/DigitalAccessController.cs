using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/books")]
[Authorize(Policy = PolicyNames.BorrowBooks)]
public sealed class DigitalAccessController(LoanService service) : ControllerBase
{
    [HttpGet("{bookId}/digital-access")]
    public async Task<IActionResult> Get(string bookId, CancellationToken token)
    {
        if (!ObjectId.TryParse(bookId, out _)) return ApiProblemFactory.Result(400, "Invalid book identifier");
        var result = await service.GetDigitalAccessAsync(bookId, User.Identity?.Name ?? string.Empty, token);
        return result.ErrorCode switch
        {
            LoanOperationErrorCodes.BookNotFound => ApiProblemFactory.Result(404, "Digital book not found"),
            LoanOperationErrorCodes.Forbidden => ApiProblemFactory.Result(403, "An active digital reservation is required"),
            LoanOperationErrorCodes.InvalidUser => ApiProblemFactory.Result(403, "Digital access is not permitted"),
            _ => Ok(new DigitalAccessResponse(result.ResourceUrl!))
        };
    }
}
