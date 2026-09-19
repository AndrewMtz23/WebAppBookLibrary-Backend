using WebAppBookLibrary.Contracts.Profile;

namespace WebAppBookLibrary.Services;

public sealed class ProfileService(IUserStore users)
{
    public async Task<(int Status, ProfileResponse? Profile)> UpdateAsync(string userId, string username, UpdateProfileRequest request, CancellationToken token)
    {
        var current = await users.FindByUsernameAsync(username);
        if (current?.IsActive != true || current.Id != userId) return (401, null);
        var name = request.DisplayName?.Trim() ?? "";
        var email = request.Email?.Trim() ?? "";
        var avatar = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
        if (name.Length is < 1 or > 120 || email.Length > 254 || !EmailValidator.IsValid(email) || request.ExpectedUpdatedAt == default ||
            (avatar is not null && (avatar.Length > 2048 || !Uri.TryCreate(avatar, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))))
            return (400, null);
        var updated = await users.UpdateProfileAsync(userId, new(name, email, avatar, request.ExpectedUpdatedAt.ToUniversalTime()), token);
        return updated is null ? (409, null) : (200, Map(updated));
    }

    public async Task<ProfileResponse?> GetAsync(string username)
    {
        var user = await users.FindByUsernameAsync(username);
        if (user?.IsActive != true) return null;
        return Map(user);
    }

    private static ProfileResponse Map(WebAppBookLibrary.Models.User user) => new(
            user.Id,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
            user.Username,
            user.Email,
            user.AvatarUrl,
            user.Role,
            user.CreatedAt,
            user.LastLoginAt,
            user.UpdatedAt);
}
