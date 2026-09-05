using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Contracts.Books;

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
        Assert.DoesNotContain("PasswordHash", userFields);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 100)]
    public void BookQuery_EnforcesPublishedPaginationLimits(int requested, int expected)
    {
        Assert.Equal(expected, new BookQuery { PageSize = requested }.Normalize().PageSize);
    }
}
