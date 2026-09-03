using WebAppBookLibrary.Security;

namespace WebAppBookLibrary.Tests;

public class RoleNamesTests
{
    [Fact]
    public void Roles_are_canonical_lowercase_values()
    {
        Assert.Equal("user", RoleNames.User);
        Assert.Equal("librarian", RoleNames.Librarian);
        Assert.Equal("admin", RoleNames.Admin);
    }

    [Theory]
    [InlineData("User", "user")]
    [InlineData(" LIBRARIAN ", "librarian")]
    [InlineData("admin", "admin")]
    public void TryNormalize_accepts_legacy_casing(string value, string expected)
    {
        Assert.True(RoleNames.TryNormalize(value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("owner")]
    public void TryNormalize_rejects_unknown_roles(string? value)
    {
        Assert.False(RoleNames.TryNormalize(value, out _));
    }
}
