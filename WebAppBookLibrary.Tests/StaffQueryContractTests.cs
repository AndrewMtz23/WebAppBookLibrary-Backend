using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Services;
using MongoDB.Bson;

namespace WebAppBookLibrary.Tests;

public sealed class StaffQueryContractTests
{
    [Fact]
    public void Null_sort_and_direction_from_binding_use_defaults()
    {
        var book = new BookQuery { Sort = null!, Direction = null! }.Normalize();
        Assert.Equal("createdAt", book.Sort); Assert.Equal("desc", book.Direction);
        var admin = new AdminUserQuery { Sort = null!, Direction = null! }.Normalize();
        Assert.Equal("username", admin.Sort); Assert.Equal("asc", admin.Direction);
        var loan = new LoanQuery { Sort = null!, Direction = null!, DateField = null! }.Normalize();
        Assert.Equal("reservedAt", loan.Sort); Assert.Equal("desc", loan.Direction);
    }
    [Fact]
    public void Book_query_normalizes_staff_filters_and_bounds_text()
    {
        var query = new BookQuery { Query = new string('x', 250), IsActive = false, LowStock = true, MissingResource = true, Page = 0, PageSize = 500 }.Normalize();
        Assert.Equal(200, query.Query!.Length);
        Assert.False(query.IsActive);
        Assert.True(query.LowStock);
        Assert.True(query.MissingResource);
        Assert.Equal(1, query.Page);
        Assert.Equal(100, query.PageSize);
    }

    [Fact]
    public void Admin_user_query_normalizes_dates_sort_and_direction()
    {
        var query = new AdminUserQuery { Query = new string('a', 250), CreatedFrom = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Local), CreatedTo = new DateTime(2026, 2, 1), Sort = "lastLoginAt", Direction = "asc" }.Normalize();
        Assert.Equal(200, query.Query!.Length);
        Assert.Equal(DateTimeKind.Utc, query.CreatedFrom!.Value.Kind);
        Assert.Equal("lastLoginAt", query.Sort);
        Assert.Equal("asc", query.Direction);
    }

    [Fact]
    public void Admin_user_query_rejects_inverted_intervals()
    {
        var query = new AdminUserQuery { LastLoginFrom = new DateTime(2026, 2, 1), LastLoginTo = new DateTime(2026, 1, 1) };
        Assert.Contains(query.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(query)), error => error.MemberNames.Contains(nameof(AdminUserQuery.LastLoginTo)));
    }

    [Fact]
    public void Loan_query_normalizes_operational_fields_and_bounds_text()
    {
        var query = new LoanQuery { Query = new string('q', 250), DueFrom = new DateTime(2026, 1, 1), DueTo = new DateTime(2026, 2, 1), DateField = "returnedAt", Sort = "dueAt", Direction = "asc" }.Normalize();
        Assert.Equal(200, query.Query!.Length);
        Assert.Equal("returnedAt", query.DateField);
        Assert.Equal("dueAt", query.Sort);
        Assert.Equal("asc", query.Direction);
        Assert.True(query.IncludeIdTieBreaker);
    }

    [Fact]
    public void Loan_query_rejects_inverted_due_interval()
    {
        var query = new LoanQuery { DueFrom = new DateTime(2026, 2, 1), DueTo = new DateTime(2026, 1, 1) };
        Assert.Contains(query.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(query)), error => error.MemberNames.Contains(nameof(LoanQuery.DueTo)));
    }

    [Fact]
    public void Book_store_filter_contains_operational_predicates_and_escaped_search()
    {
        var json = MongoBookStore.RenderFilter(new BookQuery { Query = ".*", IsActive = false, LowStock = true, MissingResource = true }.Normalize(), true).ToJson();
        Assert.Contains("IsActive", json);
        Assert.Contains("AvailableCopies", json);
        Assert.Contains("DigitalResourceUrl", json);
        Assert.Contains(@"\\.\\*", json);
    }

    [Fact]
    public void Reader_book_filter_forces_active_and_ignores_staff_visibility_filters()
    {
        var json = MongoBookStore.RenderFilter(new BookQuery { IsActive = false, LowStock = true }.Normalize(), false).ToJson();
        Assert.Contains("IsActive", json);
        Assert.DoesNotContain("AvailableCopies", json);
        Assert.Contains("$exists", json);
    }
}
