using System.Security.Claims;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Security;

public static class CurrentAccountValidator
{
    public static async Task<bool> ValidateAsync(ClaimsPrincipal principal, IUserStore store)
    {
        if (principal.Identity?.IsAuthenticated != true) return true;
        var username = principal.Identity.Name;
        var tokenRole = principal.FindFirstValue(ClaimTypes.Role);
        if (string.IsNullOrWhiteSpace(username) || !RoleNames.TryNormalize(tokenRole, out var normalizedTokenRole)) return false;
        var user = await store.FindByUsernameAsync(username);
        return user?.IsActive == true && RoleNames.TryNormalize(user.Role, out var currentRole) && currentRole == normalizedTokenRole;
    }
}
