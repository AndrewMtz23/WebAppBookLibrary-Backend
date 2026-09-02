using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Contracts.Loans;
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
        var username = User.Identity?.Name;
        var result = await _loanService.CreateLoanAsync(request.BookId, username!);

        if (!result.Success)
            return BadRequest(new { error = result.Message });

        return Ok(new
        {
            message = result.Message,
            data = result.Loan
        });
    }

    [HttpGet("my")]
    [Authorize(Policy = PolicyNames.BorrowBooks)]
    public async Task<IActionResult> GetMyLoans()
    {
        var username = User.Identity?.Name;
        var loans = await _loanService.GetLoansByUsernameAsync(username!);

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
        var username = User.Identity?.Name;
        var result = await _loanService.MarkAsReturnedAsync(id, username!);

        if (!result.Success)
            return NotFound(new { error = result.Message });

        return Ok(new { message = result.Message });
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = PolicyNames.DeleteBooks)]
    public async Task<IActionResult> DeleteLoan(string id)
    {
        var result = await _loanService.DeleteLoanAsync(id);
        if (!result.Success)
            return NotFound(new { error = result.Message });

        return Ok(new { message = result.Message });
    }
}
