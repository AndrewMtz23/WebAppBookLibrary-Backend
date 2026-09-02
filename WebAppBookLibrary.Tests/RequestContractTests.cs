using System.ComponentModel.DataAnnotations;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Loans;

namespace WebAppBookLibrary.Tests;

public class RequestContractTests
{
    [Fact]
    public void Book_input_does_not_allow_identity_or_availability_assignment()
    {
        var names = typeof(UpsertBookRequest).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain("Id", names);
        Assert.DoesNotContain("IsAvailable", names);
        Assert.Equal(["Author", "Genre", "Title", "Year"], names.Order());
    }

    [Fact]
    public void Loan_input_exposes_only_book_identifier()
    {
        var names = typeof(CreateLoanRequest).GetProperties().Select(property => property.Name).ToArray();

        Assert.Equal(["BookId"], names);
    }

    [Theory]
    [InlineData("", "Author", 2020, "Genre")]
    [InlineData("Title", "", 2020, "Genre")]
    [InlineData("Title", "Author", 999, "Genre")]
    [InlineData("Title", "Author", 2101, "Genre")]
    public void Book_input_rejects_invalid_required_values_or_year(
        string title,
        string author,
        int? year,
        string genre)
    {
        var request = new UpsertBookRequest
        {
            Title = title,
            Author = author,
            Year = year,
            Genre = genre
        };

        Assert.False(IsValid(request));
    }

    [Fact]
    public void Book_input_rejects_fields_over_their_maximum_lengths()
    {
        Assert.False(IsValid(new UpsertBookRequest
        {
            Title = new string('t', 201),
            Author = "Author",
            Genre = "Genre"
        }));
        Assert.False(IsValid(new UpsertBookRequest
        {
            Title = "Title",
            Author = new string('a', 201),
            Genre = "Genre"
        }));
        Assert.False(IsValid(new UpsertBookRequest
        {
            Title = "Title",
            Author = "Author",
            Genre = new string('g', 101)
        }));
    }

    [Fact]
    public void Loan_input_requires_book_identifier()
    {
        var request = new CreateLoanRequest { BookId = "" };

        Assert.False(IsValid(request));
    }

    private static bool IsValid(object instance)
    {
        return Validator.TryValidateObject(
            instance,
            new ValidationContext(instance),
            new List<ValidationResult>(),
            validateAllProperties: true);
    }
}
