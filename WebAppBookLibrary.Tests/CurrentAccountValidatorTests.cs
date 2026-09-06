using System.Security.Claims;
using Moq;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class CurrentAccountValidatorTests
{
    [Theory]
    [InlineData(false, RoleNames.Admin, RoleNames.Admin)]
    [InlineData(true, RoleNames.User, RoleNames.Admin)]
    public async Task ValidateAsync_RejectsInactiveOrRoleChangedAccounts(bool active, string storedRole, string tokenRole)
    {
        var store = new Mock<IUserStore>();
        store.Setup(x => x.FindByUsernameAsync("ana")).ReturnsAsync(new User { Username = "ana", IsActive = active, Role = storedRole });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "ana"), new Claim(ClaimTypes.Role, tokenRole)
        ], "test"));

        Assert.False(await CurrentAccountValidator.ValidateAsync(principal, store.Object));
    }
}
