using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using WebAppBookLibrary.Security;

namespace WebAppBookLibrary.Tests;

public class AuthorizationPolicyTests
{
    [Fact]
    public async Task BorrowBooks_accepts_user_only()
    {
        Assert.True(await IsAuthorizedAsync(PolicyNames.BorrowBooks, RoleNames.User));
        Assert.False(await IsAuthorizedAsync(PolicyNames.BorrowBooks, RoleNames.Librarian));
        Assert.False(await IsAuthorizedAsync(PolicyNames.BorrowBooks, RoleNames.Admin));
    }

    [Fact]
    public async Task ManageBooks_accepts_librarian_and_admin()
    {
        Assert.False(await IsAuthorizedAsync(PolicyNames.ManageBooks, RoleNames.User));
        Assert.True(await IsAuthorizedAsync(PolicyNames.ManageBooks, RoleNames.Librarian));
        Assert.True(await IsAuthorizedAsync(PolicyNames.ManageBooks, RoleNames.Admin));
    }

    [Fact]
    public async Task ViewAudit_accepts_admin_only()
    {
        Assert.False(await IsAuthorizedAsync(PolicyNames.ViewAudit, RoleNames.User));
        Assert.False(await IsAuthorizedAsync(PolicyNames.ViewAudit, RoleNames.Librarian));
        Assert.True(await IsAuthorizedAsync(PolicyNames.ViewAudit, RoleNames.Admin));
    }

    [Fact]
    public async Task Default_policy_accepts_only_canonical_roles()
    {
        Assert.True(await IsAuthorizedAsync(policyName: null, RoleNames.User));
        Assert.True(await IsAuthorizedAsync(policyName: null, RoleNames.Librarian));
        Assert.True(await IsAuthorizedAsync(policyName: null, RoleNames.Admin));
        Assert.False(await IsAuthorizedAsync(policyName: null, "unknown"));
    }

    private static async Task<bool> IsAuthorizedAsync(string? policyName, string role)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Program.ConfigureAuthorizationPolicies(services);

        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)],
            authenticationType: "Test");
        var user = new ClaimsPrincipal(identity);

        var result = policyName is null
            ? await authorization.AuthorizeAsync(user, resource: null, await GetDefaultPolicyAsync(provider))
            : await authorization.AuthorizeAsync(user, resource: null, policyName);

        return result.Succeeded;
    }

    private static Task<AuthorizationPolicy> GetDefaultPolicyAsync(IServiceProvider provider)
    {
        return provider
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetDefaultPolicyAsync();
    }
}
