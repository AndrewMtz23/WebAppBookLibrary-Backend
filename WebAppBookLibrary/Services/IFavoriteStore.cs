using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public interface IFavoriteStore
{
    Task<User?> FindActiveUserAsync(string username, CancellationToken token);
    Task<bool> ActiveBookExistsAsync(string bookId, CancellationToken token);
    Task<Favorite?> FindAsync(string userId, string bookId, CancellationToken token);
    Task<IReadOnlyList<Favorite>> ListAsync(string userId, CancellationToken token);
    Task InsertAsync(Favorite favorite, CancellationToken token);
    Task<bool> DeleteAsync(string userId, string bookId, CancellationToken token);
}
