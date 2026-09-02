using Moq;
using MongoDB.Driver;
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
    [InlineData("ana", "new@example.com", "ana", "ana@example.com")]
    [InlineData("new", "ana@example.com", "ana", "ana@example.com")]
    public async Task CreateUser_rejects_duplicate_username_or_email(
        string username,
        string email,
        string existingUsername,
        string existingEmail)
    {
        var store = new Mock<IUserStore>();
        store.Setup(x => x.FindByUsernameOrEmailAsync(username, email))
            .ReturnsAsync(new User { Username = existingUsername, Email = existingEmail });

        var service = new UserService(store.Object);
        var result = await service.CreateUserAsync(new RegisterRequest(username, "Secure1", email));

        Assert.False(result.Success);
        Assert.Equal("DuplicateUser", result.ErrorCode);
        Assert.Null(result.User);
        store.Verify(x => x.InsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task CreateUser_returns_duplicate_result_when_insert_hits_unique_index_race()
    {
        var store = new Mock<IUserStore>();
        store.Setup(x => x.FindByUsernameOrEmailAsync("ana", "ana@example.com"))
            .ReturnsAsync((User?)null);
        store.Setup(x => x.InsertAsync(It.IsAny<User>()))
            .ThrowsAsync(CreateDuplicateKeyException());

        var service = new UserService(store.Object);
        var result = await service.CreateUserAsync(
            new RegisterRequest("ana", "Secure1", "ana@example.com"));

        Assert.False(result.Success);
        Assert.Equal("DuplicateUser", result.ErrorCode);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task GetUserByUserName_uses_exact_username_lookup_when_legacy_empty_email_exists()
    {
        var expectedUser = new User { Username = "ana", Email = "ana@example.com", IsActive = true };
        var legacyUser = new User { Username = "other", Email = string.Empty, IsActive = true };
        var store = new Mock<IUserStore>();
        store.Setup(x => x.FindByUsernameAsync("ana"))
            .ReturnsAsync(expectedUser);
        store.Setup(x => x.FindByUsernameOrEmailAsync("ana", string.Empty))
            .ReturnsAsync(legacyUser);

        var service = new UserService(store.Object);
        var result = await service.GetUserByUserNameAsync("ana");

        Assert.Same(expectedUser, result);
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

    private static MongoWriteException CreateDuplicateKeyException()
    {
        var constructor = typeof(WriteError).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(ServerErrorCategory), typeof(int), typeof(string), typeof(MongoDB.Bson.BsonDocument) },
            modifiers: null)!;
        var writeError = (WriteError)constructor.Invoke(new object?[]
        {
            ServerErrorCategory.DuplicateKey,
            11000,
            "duplicate key",
            null
        });

        var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
            new MongoDB.Driver.Core.Servers.ServerId(
                new MongoDB.Driver.Core.Clusters.ClusterId(),
                new System.Net.DnsEndPoint("localhost", 27017)));

        return new MongoWriteException(connectionId, writeError, null!, null!);
    }
}
