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
}
