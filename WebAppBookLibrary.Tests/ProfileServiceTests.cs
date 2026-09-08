using Moq;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class ProfileServiceTests
{
    [Fact]
    public async Task GetAsync_maps_only_active_account_identity()
    {
        var createdAt = new DateTime(2025, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        var lastLoginAt = new DateTime(2026, 9, 6, 15, 0, 0, DateTimeKind.Utc);
        var store = new Mock<IUserStore>();
        store.Setup(item => item.FindByUsernameAsync("ana")).ReturnsAsync(new User
        {
            Id = "u1", Username = "ana", DisplayName = "Ana M.", Email = "ana@example.com",
            Role = "user", CreatedAt = createdAt, LastLoginAt = lastLoginAt, IsActive = true,
            PasswordHash = "never expose"
        });
        var service = new ProfileService(store.Object);

        var result = await service.GetAsync("ana");

        Assert.NotNull(result);
        Assert.Equal("u1", result.Id);
        Assert.Equal("Ana M.", result.DisplayName);
        Assert.Equal(createdAt, result.CreatedAt);
        Assert.Equal(lastLoginAt, result.LastLoginAt);
    }

    [Fact]
    public async Task GetAsync_rejects_inactive_account()
    {
        var store = new Mock<IUserStore>();
        store.Setup(item => item.FindByUsernameAsync("ana"))
            .ReturnsAsync(new User { Username = "ana", IsActive = false });
        var service = new ProfileService(store.Object);

        Assert.Null(await service.GetAsync("ana"));
    }
}
