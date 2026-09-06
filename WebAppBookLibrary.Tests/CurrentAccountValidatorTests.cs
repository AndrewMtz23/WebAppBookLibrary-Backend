using System.Security.Claims;
using Moq;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class CurrentAccountValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsMatchingActiveAccount()
    {
        var store = new Mock<IUserStore>();
        store.Setup(x => x.FindByUsernameAsync("ana")).ReturnsAsync(new User { Id = "u1", Username = "ana", IsActive = true, Role = RoleNames.User });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "ana"),
            new Claim(ClaimTypes.NameIdentifier, "u1"),
            new Claim(ClaimTypes.Role, RoleNames.User)
        ], "test"));

        Assert.True(await CurrentAccountValidator.ValidateAsync(principal, store.Object));
    }

    [Fact]
    public async Task ValidateAsync_RejectsTokenForDifferentAccountId()
    {
        var store = new Mock<IUserStore>();
        store.Setup(x => x.FindByUsernameAsync("ana")).ReturnsAsync(new User { Id = "u1", Username = "ana", IsActive = true, Role = RoleNames.User });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "ana"),
            new Claim(ClaimTypes.NameIdentifier, "another-user"),
            new Claim(ClaimTypes.Role, RoleNames.User)
        ], "test"));

        Assert.False(await CurrentAccountValidator.ValidateAsync(principal, store.Object));
    }

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
