using System.ComponentModel.DataAnnotations;

namespace WebAppBookLibrary.Contracts.Loans;

public sealed class CreateLoanRequest
{
    [Required]
    public string BookId { get; init; } = string.Empty;
}
