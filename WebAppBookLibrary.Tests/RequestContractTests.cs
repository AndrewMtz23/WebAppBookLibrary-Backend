using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Tests;

public class RequestContractTests
{
    [Fact]
    public void Admin_user_contract_exposes_profile_image_and_full_update_request()
    {
        Assert.Contains(typeof(AdminUserResponse).GetProperties(), property => property.Name == "AvatarUrl");
        var requestType = typeof(AdminUserResponse).Assembly.GetType("WebAppBookLibrary.Contracts.Admin.UpdateAdminUserRequest");
        Assert.NotNull(requestType);
        Assert.Equal(
            ["AvatarUrl", "DisplayName", "Email", "ExpectedUpdatedAt", "IsActive", "Role", "Username"],
            requestType!.GetProperties().Select(property => property.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/avatar.jpg")]
    [InlineData("ftp://example.test/avatar.jpg")]
    public void Admin_user_update_rejects_unsafe_avatar_urls(string avatarUrl)
    {
        var request = new UpdateAdminUserRequest
        {
            Username = "ana-reader",
            DisplayName = "Ana Reader",
            Email = "ana@example.test",
            AvatarUrl = avatarUrl,
            Role = "user",
            IsActive = true,
            ExpectedUpdatedAt = DateTime.UtcNow
        };

        Assert.False(IsValid(request));
    }

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

    [Fact]
    public void Book_response_does_not_expose_active_loan_correlation()
    {
        var json = JsonSerializer.Serialize(new Book
        {
            Id = "507f1f77bcf86cd799439011",
            ActiveLoanId = "507f1f77bcf86cd799439012"
        });

        Assert.DoesNotContain("ActiveLoanId", json, StringComparison.OrdinalIgnoreCase);
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
