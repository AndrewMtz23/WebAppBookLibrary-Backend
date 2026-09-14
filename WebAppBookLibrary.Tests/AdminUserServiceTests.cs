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
    public void Admin_user_service_and_store_expose_full_profile_update()
    {
        Assert.Contains(typeof(AdminUserService).GetMethods(), method => method.Name == "UpdateAsync");
        Assert.Contains(typeof(IAdminUserStore).GetMethods(), method => method.Name == "UpdateSafelyAsync");
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

    [Fact]
    public async Task UpdateAsync_normalizes_and_updates_the_complete_profile()
    {
        var store = new FakeAdminStore { Target = User("user1", RoleNames.User) };
        var service = new AdminUserService(store);
        var expected = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var result = await service.UpdateAsync("admin1", "user1", new UpdateAdminUserRequest
        {
            Username = "  ana.reader  ",
            DisplayName = "  Ana Reader  ",
            Email = "  ana@example.test  ",
            AvatarUrl = "  https://images.example.test/ana.jpg  ",
            Role = "LIBRARIAN",
            IsActive = true,
            ExpectedUpdatedAt = expected
        }, expected.AddMinutes(1), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ana.reader", store.LastUpdate!.Username);
        Assert.Equal("Ana Reader", store.LastUpdate.DisplayName);
        Assert.Equal("ana@example.test", store.LastUpdate.Email);
        Assert.Equal("https://images.example.test/ana.jpg", store.LastUpdate.AvatarUrl);
        Assert.Equal(RoleNames.Librarian, store.LastUpdate.Role);
    }

    [Fact]
    public async Task UpdateAsync_rejects_an_unsafe_avatar_before_writing()
    {
        var store = new FakeAdminStore { Target = User("user1", RoleNames.User) };
        var result = await new AdminUserService(store).UpdateAsync("admin1", "user1", new UpdateAdminUserRequest
        {
            Username = "ana.reader", DisplayName = "Ana", Email = "ana@example.test",
            AvatarUrl = "javascript:alert(1)", Role = "user", IsActive = true, ExpectedUpdatedAt = DateTime.UtcNow
        }, DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AdminUserErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Null(store.LastUpdate);
    }

    private static User User(string id, string role) => new() { Id = id, Username = id, Email = $"{id}@example.com", Role = role, IsActive = true };

    private sealed class FakeAdminStore : IAdminUserStore
    {
        public User? Target { get; init; }
        public long ActiveAdmins { get; init; } = 2;
        public int Updates { get; private set; }
        public (string Id, string Role, DateTime At) LastRoleUpdate { get; private set; }
        public AdminUserUpdateCommand? LastUpdate { get; private set; }
        public Task<PagedResult<User>> SearchAsync(AdminUserQuery query, CancellationToken token) => Task.FromResult(new PagedResult<User>([], 1, 20, 0));
        public Task<User?> FindByIdAsync(string id, CancellationToken token) => Task.FromResult(Target);
        public Task<long> CountActiveAdminsAsync(CancellationToken token) => Task.FromResult(ActiveAdmins);
        public Task<bool> TrySetRoleAsync(string id, string role, DateTime updatedAtUtc, CancellationToken token) { Updates++; LastRoleUpdate = (id, role, updatedAtUtc); return Task.FromResult(true); }
        public Task<bool> TrySetStatusAsync(string id, bool active, DateTime updatedAtUtc, CancellationToken token) { Updates++; return Task.FromResult(true); }
        public Task<AdminStoreMutationResult> SetRoleSafelyAsync(string actorId, string targetId, string role, DateTime updatedAtUtc, CancellationToken token)
        {
            if (actorId == targetId && role != RoleNames.Admin) return Task.FromResult(AdminStoreMutationResult.SelfMutation);
            if (Target?.Role == RoleNames.Admin && Target.IsActive && role != RoleNames.Admin && ActiveAdmins <= 1) return Task.FromResult(AdminStoreMutationResult.LastAdmin);
            if (Target is null) return Task.FromResult(AdminStoreMutationResult.NotFound);
            Updates++; LastRoleUpdate = (targetId, role, updatedAtUtc); return Task.FromResult(AdminStoreMutationResult.Success);
        }
        public Task<AdminStoreMutationResult> SetStatusSafelyAsync(string actorId, string targetId, bool active, DateTime updatedAtUtc, CancellationToken token)
        {
            if (actorId == targetId && !active) return Task.FromResult(AdminStoreMutationResult.SelfMutation);
            if (Target is null) return Task.FromResult(AdminStoreMutationResult.NotFound);
            Updates++; return Task.FromResult(AdminStoreMutationResult.Success);
        }
        public Task<AdminStoreMutationResult> UpdateSafelyAsync(string actorId, string targetId, AdminUserUpdateCommand command, DateTime updatedAtUtc, CancellationToken token)
        {
            if (actorId == targetId && (command.Role != RoleNames.Admin || !command.IsActive)) return Task.FromResult(AdminStoreMutationResult.SelfMutation);
            if (Target is null) return Task.FromResult(AdminStoreMutationResult.NotFound);
            Updates++; LastUpdate = command;
            var updated = new User
            {
                Id = targetId, Username = command.Username, DisplayName = command.DisplayName, Email = command.Email,
                AvatarUrl = command.AvatarUrl, Role = command.Role, IsActive = command.IsActive, UpdatedAt = updatedAtUtc
            };
            return Task.FromResult(new AdminStoreMutationResult(AdminStoreMutationOutcome.Success, "admin", Target.Username, Target.Role, Target.IsActive, command.Role, command.IsActive, updated));
        }
    }
}
