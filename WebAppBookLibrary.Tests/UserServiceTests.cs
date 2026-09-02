using Moq;
using WebAppBookLibrary.Contracts.Auth;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public class UserServiceTests
{
    [Fact]
    public async Task CreateUser_assigns_user_role()
    {
        var store = new Mock<IUserStore>();
        store.Setup(x => x.FindByUsernameOrEmailAsync("ana", "ana@example.com"))
            .ReturnsAsync((User?)null);
        User? inserted = null;
        store.Setup(x => x.InsertAsync(It.IsAny<User>()))
            .Callback<User>(user => inserted = user)
            .Returns(Task.CompletedTask);

        var service = new UserService(store.Object);
        var result = await service.CreateUserAsync(
            new RegisterRequest("ana", "Secure1", "ana@example.com"));

        Assert.True(result.Success);
        Assert.Equal(RoleNames.User, inserted!.Role);
        Assert.True(PasswordHasher.VerifyPassword("Secure1", inserted.PasswordHash));
    }

    [Fact]
    public void RegisterRequest_has_no_role_property()
    {
        Assert.DoesNotContain(typeof(RegisterRequest).GetProperties(), property => property.Name == "Role");
    }

    [Theory]
    [InlineData("ana", "ana@example.com")]
    [InlineData("bea", "bea@example.com")]
    public async Task CreateUser_rejects_duplicate_username_or_email(string username, string email)
    {
        var store = new Mock<IUserStore>();
        store.Setup(x => x.FindByUsernameOrEmailAsync(username, email))
            .ReturnsAsync(new User { Username = username, Email = email });

        var service = new UserService(store.Object);
        var result = await service.CreateUserAsync(new RegisterRequest(username, "Secure1", email));

        Assert.False(result.Success);
        Assert.Equal("DuplicateUser", result.ErrorCode);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task CreateUser_rejects_invalid_email()
    {
        var service = new UserService(new Mock<IUserStore>().Object);

        var result = await service.CreateUserAsync(
            new RegisterRequest("ana", "Secure1", "not-an-email"));

        Assert.False(result.Success);
        Assert.Equal("Validation", result.ErrorCode);
        Assert.Null(result.User);
    }

    [Theory]
    [InlineData("secure1")]
    [InlineData("SECURE1")]
    [InlineData("Secure")]
    public async Task CreateUser_rejects_password_without_required_character_class(string password)
    {
        var service = new UserService(new Mock<IUserStore>().Object);

        var result = await service.CreateUserAsync(
            new RegisterRequest("ana", password, "ana@example.com"));

        Assert.False(result.Success);
        Assert.Equal("Validation", result.ErrorCode);
        Assert.Null(result.User);
    }
}
