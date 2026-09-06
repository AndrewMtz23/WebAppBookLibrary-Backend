using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Loans;

namespace WebAppBookLibrary.Tests;

public sealed class Phase2ContractTests
{
    [Fact]
    public void PublicBookAndUserDtos_DoNotExposePersistenceOrCredentialFields()
    {
        var bookFields = typeof(BookDetailResponse).GetProperties().Select(property => property.Name).ToArray();
        var userFields = typeof(AdminUserResponse).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain("ActiveLoanId", bookFields);
        Assert.DoesNotContain("SchemaVersion", bookFields);
        Assert.DoesNotContain("DigitalResourceUrl", bookFields);
        Assert.DoesNotContain("PasswordHash", userFields);
    }

    [Fact]
    public void LoanResponse_DoesNotExposePersistenceCorrelationOrLegacyFields()
    {
        var fields = typeof(LoanResponse).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("ActiveReservationKey", fields);
        Assert.DoesNotContain("CreatedBy", fields);
        Assert.DoesNotContain("LoanDate", fields);
        Assert.DoesNotContain("IsReturned", fields);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 100)]
    public void BookQuery_EnforcesPublishedPaginationLimits(int requested, int expected)
    {
        Assert.Equal(expected, new BookQuery { PageSize = requested }.Normalize().PageSize);
    }

    [Fact]
    public void LoanQuery_ClampsPaginationAndNormalizesCanonicalFilters()
    {
        var query = new LoanQuery { Page = 0, PageSize = 500, Status = " ACTIVE ", MediaType = " PHYSICAL " }.Normalize();
        Assert.Equal(1, query.Page);
        Assert.Equal(100, query.PageSize);
        Assert.Equal("active", query.Status);
        Assert.Equal("physical", query.MediaType);
    }
}
