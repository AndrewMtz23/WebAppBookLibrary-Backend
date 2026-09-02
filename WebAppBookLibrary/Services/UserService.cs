using WebAppBookLibrary.Contracts.Auth;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;

namespace WebAppBookLibrary.Services;

public sealed class UserService
{
    private readonly IUserStore _userStore;

    public UserService(IUserStore userStore)
    {
        _userStore = userStore;
    }

    public async Task<User?> GetUserByUserNameAsync(string username)
    {
        var user = await _userStore.FindByUsernameOrEmailAsync(username, string.Empty);
        return user?.IsActive == true ? user : null;
    }

    public async Task<UserCreationResult> CreateUserAsync(RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) ||
            !EmailValidator.IsValid(request.Email) ||
            !PasswordValidator.IsValid(request.Password))
        {
            return new UserCreationResult(false, UserCreationErrorCodes.Validation, null);
        }

        var existingUser = await _userStore.FindByUsernameOrEmailAsync(request.Username, request.Email);
        if (existingUser != null)
        {
            return new UserCreationResult(false, UserCreationErrorCodes.DuplicateUser, null);
        }

        var user = new User
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = PasswordHasher.HashPassword(request.Password),
            Role = RoleNames.User
        };

        await _userStore.InsertAsync(user);
        return new UserCreationResult(true, string.Empty, user);
    }
}

public sealed record UserCreationResult(bool Success, string ErrorCode, User? User);

public static class UserCreationErrorCodes
{
    public const string Validation = "Validation";
    public const string DuplicateUser = "DuplicateUser";
}
