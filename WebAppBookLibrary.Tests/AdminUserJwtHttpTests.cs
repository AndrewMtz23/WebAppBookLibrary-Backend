using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class AdminUserJwtHttpTests
{
    [Fact]
    public async Task Previously_issued_admin_Jwt_is_rejected_after_role_changes()
    {
        const string key = "task-four-test-key-that-is-long-enough-123456789";
        const string userId = "507f1f77bcf86cd799439011";
        var currentRole = RoleNames.Admin;
        var users = new Mock<IUserStore>();
        users.Setup(store => store.FindByUsernameAsync("staff")).ReturnsAsync(() => new User { Id = userId, Username = "staff", IsActive = true, Role = currentRole });
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(users.Object);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = Parameters(key));
        Program.ConfigureAuthorizationPolicies(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (!await CurrentAccountValidator.ValidateAsync(context.User, context.RequestServices.GetRequiredService<IUserStore>()))
            {
                await ApiProblemFactory.WriteAsync(context, 401, "Session is no longer valid", context.RequestAborted);
                return;
            }
            await next(context);
        });
        app.UseAuthorization();
        app.MapGet("/manage-users", () => Results.Ok()).RequireAuthorization(PolicyNames.ManageUsers);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(key, userId));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/manage-users")).StatusCode);

            currentRole = RoleNames.User;

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/manage-users")).StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Previously_issued_admin_Jwt_is_rejected_after_account_deactivation()
    {
        const string key = "task-four-test-key-that-is-long-enough-123456789";
        const string userId = "507f1f77bcf86cd799439011";
        var active = true;
        var users = new Mock<IUserStore>();
        users.Setup(store => store.FindByUsernameAsync("staff")).ReturnsAsync(() => new User { Id = userId, Username = "staff", IsActive = active, Role = RoleNames.Admin });
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(users.Object);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = Parameters(key));
        Program.ConfigureAuthorizationPolicies(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (!await CurrentAccountValidator.ValidateAsync(context.User, context.RequestServices.GetRequiredService<IUserStore>()))
            {
                await ApiProblemFactory.WriteAsync(context, 401, "Session is no longer valid", context.RequestAborted);
                return;
            }
            await next(context);
        });
        app.UseAuthorization();
        app.MapGet("/manage-users", () => Results.Ok()).RequireAuthorization(PolicyNames.ManageUsers);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(key, userId));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/manage-users")).StatusCode);
            active = false;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/manage-users")).StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    private static TokenValidationParameters Parameters(string key) => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "tests",
        ValidAudience = "tests",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
        RoleClaimType = ClaimTypes.Role,
        NameClaimType = ClaimTypes.Name
    };

    private static string Token(string key, string userId)
    {
        var token = new JwtSecurityToken(
            issuer: "tests",
            audience: "tests",
            claims: [new(ClaimTypes.Name, "staff"), new(ClaimTypes.NameIdentifier, userId), new(ClaimTypes.Role, RoleNames.Admin)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
