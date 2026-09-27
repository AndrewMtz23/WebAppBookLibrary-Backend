using System.Security.Claims;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Security;

public static class CurrentAccountValidator
{
    public static async Task<bool> ValidateAsync(ClaimsPrincipal principal, IUserStore store)
    {
        if (principal.Identity?.IsAuthenticated != true) return true;
        var username = principal.Identity.Name;
        var tokenUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tokenRole = principal.FindFirstValue(ClaimTypes.Role);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(tokenUserId) || !RoleNames.TryNormalize(tokenRole, out var normalizedTokenRole)) return false;
        var user = await store.FindByUsernameAsync(username);
        return user?.IsActive == true && user.Id == tokenUserId && CredentialVersion(principal) == user.CredentialVersion && RoleNames.TryNormalize(user.Role, out var currentRole) && currentRole == normalizedTokenRole;
    }

    public static long? CredentialVersion(ClaimsPrincipal principal)
    {
        var claims = principal.FindAll("credential_version").ToArray();
        if (claims.Length == 0) return 0; // Existing JWTs are compatible only until the first credential change.
        if (claims.Length != 1) return null;
        return long.TryParse(claims[0].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version) && version >= 0 ? version : null;
    }
}
