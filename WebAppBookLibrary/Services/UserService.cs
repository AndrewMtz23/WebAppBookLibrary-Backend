using WebAppBookLibrary.Contracts.Auth;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using MongoDB.Driver;

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
        var user = await _userStore.FindByUsernameAsync(username);
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

        try
        {
            await _userStore.InsertAsync(user);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new UserCreationResult(false, UserCreationErrorCodes.DuplicateUser, null);
        }

        return new UserCreationResult(true, string.Empty, user);
    }
}

public sealed record UserCreationResult(bool Success, string ErrorCode, User? User);

public static class UserCreationErrorCodes
{
    public const string Validation = "Validation";
    public const string DuplicateUser = "DuplicateUser";
}
