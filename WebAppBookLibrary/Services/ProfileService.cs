using WebAppBookLibrary.Contracts.Profile;

namespace WebAppBookLibrary.Services;

public sealed class ProfileService(IUserStore users)
{
    public async Task<ProfileResponse?> GetAsync(string username)
    {
        var user = await users.FindByUsernameAsync(username);
        if (user?.IsActive != true) return null;
        return new(
            user.Id,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
            user.Username,
            user.Email,
            user.Role,
            user.CreatedAt,
            user.LastLoginAt);
    }
}
