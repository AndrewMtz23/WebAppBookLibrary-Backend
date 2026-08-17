using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers
{
    [ApiController]
    [Route("[controller]")]
    [Authorize]
    public class LoansController : ControllerBase
    {
        private readonly LoanService _loanService;

        public LoansController(LoanService loanService)
        {
            _loanService = loanService;
        }

        // Clase DTO para evitar error de "Id is required"
        public class LoanRequest
        {
            public string BookId { get; set; } = null!;
        }

        // POST /loans
        [HttpPost]
        [Authorize(Roles = "user, User")]
        public async Task<IActionResult> CreateLoan([FromBody] LoanRequest request)
        {
            var username = User.Identity?.Name;

            if (string.IsNullOrEmpty(request.BookId))
                return BadRequest(new { error = "BookId is required." });

            var result = await _loanService.CreateLoanAsync(request.BookId, username!);

            if (!result.Success)
                return BadRequest(new { error = result.Message });

            return Ok(new
            {
                message = result.Message,
                data = result.Loan
            });
        }

        // GET /loans/my
        [HttpGet("my")]
        [Authorize(Roles = "user, User")]
        public async Task<IActionResult> GetMyLoans()
        {
            var username = User.Identity?.Name;
            var loans = await _loanService.GetLoansByUsernameAsync(username!);

            return Ok(new { message = "My loans retrieved", data = loans });
        }

        // GET /loans
        [HttpGet]
        [Authorize(Roles = "admin, Admin, Librarian, librarian")]
        public async Task<IActionResult> GetAllLoans()
        {
            
            var loans = await _loanService.GetAllLoansWithDetailsAsync();
            return Ok(new { message = "All loans retrieved", data = loans });
        }

        // PUT /loans/{id}/return
        [HttpPut("{id}/return")]
        [Authorize(Roles = "admin, Admin, librarian, Librarian, user, User")]
        public async Task<IActionResult> ReturnLoan(string id)
        {
            var username = User.Identity?.Name;
            var result = await _loanService.MarkAsReturnedAsync(id, username!);

            if (!result.Success)
                return NotFound(new { error = result.Message });

            return Ok(new { message = result.Message });
        }

        // DELETE /loans/{id}
        [HttpDelete("{id}")]
        [Authorize(Roles = "admin, Admin")]
        public async Task<IActionResult> DeleteLoan(string id)
        {
            var result = await _loanService.DeleteLoanAsync(id);
            if (!result.Success)
                return NotFound(new { error = result.Message });

            return Ok(new { message = result.Message });
        }
    }
}
