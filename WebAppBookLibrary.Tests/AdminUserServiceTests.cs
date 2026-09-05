using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class AdminUserServiceTests
{
    [Fact]
    public void AdminUserResponse_DoesNotExposePasswordHash()
    {
        Assert.DoesNotContain(typeof(AdminUserResponse).GetProperties(), property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SetStatusAsync_RejectsSelfDeactivation()
    {
        var service = new AdminUserService(new FakeAdminStore());

        var result = await service.SetStatusAsync("admin1", "admin1", false, DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AdminUserErrorCodes.SelfMutation, result.ErrorCode);
    }

    [Fact]
    public async Task SetRoleAsync_ProtectsLastActiveAdmin()
    {
        var store = new FakeAdminStore { Target = User("admin2", RoleNames.Admin), ActiveAdmins = 1 };
        var service = new AdminUserService(store);

        var result = await service.SetRoleAsync("admin1", "admin2", RoleNames.User, DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AdminUserErrorCodes.LastAdmin, result.ErrorCode);
        Assert.Equal(0, store.Updates);
    }

    [Fact]
    public async Task SetRoleAsync_UpdatesValidRoleAndTimestamp()
    {
        var store = new FakeAdminStore { Target = User("user1", RoleNames.User), ActiveAdmins = 2 };
        var service = new AdminUserService(store);
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        var result = await service.SetRoleAsync("admin1", "user1", RoleNames.Librarian, now, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(("user1", RoleNames.Librarian, now), store.LastRoleUpdate);
    }

    private static User User(string id, string role) => new() { Id = id, Username = id, Email = $"{id}@example.com", Role = role, IsActive = true };

    private sealed class FakeAdminStore : IAdminUserStore
    {
        public User? Target { get; init; }
        public long ActiveAdmins { get; init; } = 2;
        public int Updates { get; private set; }
        public (string Id, string Role, DateTime At) LastRoleUpdate { get; private set; }
        public Task<PagedResult<User>> SearchAsync(AdminUserQuery query, CancellationToken token) => Task.FromResult(new PagedResult<User>([], 1, 20, 0));
        public Task<User?> FindByIdAsync(string id, CancellationToken token) => Task.FromResult(Target);
        public Task<long> CountActiveAdminsAsync(CancellationToken token) => Task.FromResult(ActiveAdmins);
        public Task<bool> TrySetRoleAsync(string id, string role, DateTime updatedAtUtc, CancellationToken token) { Updates++; LastRoleUpdate = (id, role, updatedAtUtc); return Task.FromResult(true); }
        public Task<bool> TrySetStatusAsync(string id, bool active, DateTime updatedAtUtc, CancellationToken token) { Updates++; return Task.FromResult(true); }
    }
}
