using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class FavoriteServiceTests
{
    [Fact]
    public async Task AddAsync_DuplicateFavoriteIsIdempotent()
    {
        var store = new FakeFavoriteStore { Existing = new Favorite { Id = "f1", UserId = "u1", BookId = "b1" } };
        var service = new FavoriteService(store);

        var result = await service.AddAsync("ana", "b1", new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Idempotent);
        Assert.Equal("u1", result.Favorite!.UserId);
        Assert.Equal(0, store.Insertions);
    }

    [Fact]
    public async Task AddAsync_InactiveBookReturnsNotFound()
    {
        var store = new FakeFavoriteStore { BookExists = false };
        var service = new FavoriteService(store);

        var result = await service.AddAsync("ana", "missing", DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FavoriteErrorCodes.BookNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task RemoveAsync_RepeatedDeleteIsIdempotentAndScopedToTokenUser()
    {
        var store = new FakeFavoriteStore { DeleteResult = false };
        var service = new FavoriteService(store);

        var result = await service.RemoveAsync("ana", "b1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Idempotent);
        Assert.Equal(("u1", "b1"), store.LastDelete);
    }

    private sealed class FakeFavoriteStore : IFavoriteStore
    {
        public Favorite? Existing { get; init; }
        public bool BookExists { get; init; } = true;
        public bool DeleteResult { get; init; }
        public int Insertions { get; private set; }
        public (string UserId, string BookId) LastDelete { get; private set; }
        public Task<User?> FindActiveUserAsync(string username, CancellationToken token) => Task.FromResult<User?>(new User { Id = "u1", Username = username, IsActive = true });
        public Task<bool> ActiveBookExistsAsync(string bookId, CancellationToken token) => Task.FromResult(BookExists);
        public Task<Favorite?> FindAsync(string userId, string bookId, CancellationToken token) => Task.FromResult(Existing);
        public Task<IReadOnlyList<Favorite>> ListAsync(string userId, CancellationToken token) => Task.FromResult<IReadOnlyList<Favorite>>([]);
        public Task InsertAsync(Favorite favorite, CancellationToken token) { Insertions++; return Task.CompletedTask; }
        public Task<bool> DeleteAsync(string userId, string bookId, CancellationToken token) { LastDelete = (userId, bookId); return Task.FromResult(DeleteResult); }
    }
}
