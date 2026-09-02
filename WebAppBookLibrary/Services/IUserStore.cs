using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public interface IUserStore
{
    Task<User?> FindByUsernameOrEmailAsync(string username, string email);

    Task InsertAsync(User user);
}
