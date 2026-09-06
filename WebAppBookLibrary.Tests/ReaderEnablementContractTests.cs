using System.Reflection;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class ReaderEnablementContractTests
{
    [Fact]
    public void BookQuery_preserves_reservation_count_sort()
    {
        var normalized = new BookQuery { Sort = "reservationCount", Direction = "desc" }.Normalize();

        Assert.Equal("reservationCount", normalized.Sort);
        Assert.Equal("desc", normalized.Direction);
    }

    [Fact]
    public void Reader_enablement_contracts_expose_only_expected_fields()
    {
        var assembly = typeof(BookQuery).Assembly;
        var facet = assembly.GetType("WebAppBookLibrary.Contracts.Books.BookFacetResponse");
        var profile = assembly.GetType("WebAppBookLibrary.Contracts.Profile.ProfileResponse");

        Assert.NotNull(facet);
        Assert.Equal(["Count", "Value"], PropertyNames(facet!));
        Assert.NotNull(profile);
        Assert.Equal(
            ["CreatedAt", "DisplayName", "Email", "Id", "LastLoginAt", "Role", "Username"],
            PropertyNames(profile!));
        Assert.DoesNotContain(PropertyNames(profile!), name => name.Contains("Password", StringComparison.Ordinal));
    }

    [Fact]
    public void Store_contracts_include_facets_and_last_login_mutation()
    {
        var facetMethod = typeof(IBookStore).GetMethod("GetGenreFacetsAsync");
        var lastLoginMethod = typeof(IUserStore).GetMethod("TouchLastLoginAsync");

        Assert.NotNull(facetMethod);
        Assert.Equal(typeof(CancellationToken), facetMethod!.GetParameters().Single().ParameterType);
        Assert.NotNull(lastLoginMethod);
        Assert.Equal(
            [typeof(string), typeof(DateTime), typeof(CancellationToken)],
            lastLoginMethod!.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static string[] PropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
