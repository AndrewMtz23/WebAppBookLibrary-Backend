using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public interface IUserStore
{
    Task<User?> FindByUsernameAsync(string username);

    Task<User?> FindByUsernameOrEmailAsync(string username, string email);

    Task InsertAsync(User user);

    Task TouchLastLoginAsync(string userId, DateTime occurredAtUtc, CancellationToken cancellationToken);
}
