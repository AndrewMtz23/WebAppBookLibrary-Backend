using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Data;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary;

public static class Program
{
    private const string CorsPolicyName = "_myAllowSpecificOrigins";
    private const string AuthRateLimitPolicyName = "auth";

    public static async Task Main(string[] args)
    {
        Env.Load();

        var builder = WebApplication.CreateBuilder(args);
        MapJwtEnvironmentVariables(builder.Configuration);

        var corsOrigin = GetEnvironmentVariable("CORS_ORIGIN") ?? "http://localhost:4200";
        ConfigureCors(builder.Services, corsOrigin);
        ConfigureJwt(builder.Services, builder.Configuration);
        ConfigureServices(builder.Services);
        ConfigureSwagger(builder.Services);

        var app = builder.Build();

        ConfigurePipeline(app);
        await InitializeMongoIndexes(app);
        await app.RunAsync();
    }

    public static void ConfigureAuthorizationPolicies(IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(RoleNames.User, RoleNames.Librarian, RoleNames.Admin)
                .Build();
            options.AddPolicy(
                PolicyNames.BorrowBooks,
                policy => policy.RequireRole(RoleNames.User));
            options.AddPolicy(
                PolicyNames.ManageBooks,
                policy => policy.RequireRole(RoleNames.Librarian, RoleNames.Admin));
            options.AddPolicy(
                PolicyNames.DeleteBooks,
                policy => policy.RequireRole(RoleNames.Admin));
            options.AddPolicy(
                PolicyNames.ViewAllLoans,
                policy => policy.RequireRole(RoleNames.Librarian, RoleNames.Admin));
            options.AddPolicy(
                PolicyNames.ViewAudit,
                policy => policy.RequireRole(RoleNames.Admin));
        });
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        app.UseExceptionHandler(exceptionApp =>
            exceptionApp.Run(context =>
                ApiProblemFactory.WriteAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "Internal server error",
                    context.RequestAborted)));

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseRouting();
        app.UseCors(CorsPolicyName);
        app.UseRateLimiter();
        app.UseAuthentication();

        if (app.Environment.IsDevelopment())
            app.Use(CreateDevelopmentLoggingMiddleware());

        app.UseAuthorization();
        app.MapControllers();
    }

    private static void ConfigureCors(IServiceCollection services, string corsOrigin)
    {
        services.AddCors(options =>
        {
            options.AddPolicy(CorsPolicyName, policy =>
            {
                policy.WithOrigins(corsOrigin)
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });
    }

    private static void MapJwtEnvironmentVariables(IConfiguration configuration)
    {
        MapEnvironmentVariable(configuration, "JWT_KEY", $"{JwtOptions.SectionName}:Key");
        MapEnvironmentVariable(configuration, "JWT_ISSUER", $"{JwtOptions.SectionName}:Issuer");
        MapEnvironmentVariable(configuration, "JWT_AUDIENCE", $"{JwtOptions.SectionName}:Audience");
    }

    private static void MapEnvironmentVariable(
        IConfiguration configuration,
        string environmentVariable,
        string configurationKey)
    {
        var value = GetEnvironmentVariable(environmentVariable);
        if (value is not null)
            configuration[configurationKey] = value;
    }

    private static string? GetEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void ConfigureJwt(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(ConfigureAuthenticationOptions)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
                ConfigureJwtBearerOptions(options, jwtOptions.Value));
    }

    private static void ConfigureAuthenticationOptions(AuthenticationOptions options)
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    }

    private static void ConfigureJwtBearerOptions(JwtBearerOptions options, JwtOptions jwtOptions)
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = ClaimTypes.Name
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();
                return ApiProblemFactory.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    "Unauthorized",
                    context.HttpContext.RequestAborted);
            },
            OnForbidden = context => ApiProblemFactory.WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                context.HttpContext.RequestAborted)
        };
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();
        services.AddProblemDetails();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase("LogDb"));

        services.AddSingleton<MongoDBService>();
        services.AddScoped<IUserStore, MongoUserStore>();
        services.AddScoped<ILoanStore, MongoLoanStore>();
        services.AddScoped<UserService>();
        services.AddScoped<BookService>();
        services.AddScoped<LoanService>();
        services.AddScoped<Logservice>();

        services.AddHttpContextAccessor();
        ConfigureAuthorizationPolicies(services);
        ConfigureRateLimiting(services);
    }

    private static void ConfigureRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, cancellationToken) =>
                new ValueTask(ApiProblemFactory.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    "Too many requests",
                    cancellationToken));
            options.AddPolicy(AuthRateLimitPolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });
    }

    private static void ConfigureSwagger(IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Description = "Enter a JWT bearer token.",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                }] = Array.Empty<string>()
            });
        });
    }

    private static Func<HttpContext, RequestDelegate, Task> CreateDevelopmentLoggingMiddleware()
    {
        return async (context, next) =>
        {
            Console.WriteLine($"=== REQUEST: {context.Request.Method} {context.Request.Path} ===");
            Console.WriteLine(context.User.Identity?.IsAuthenticated == true
                ? $"User authenticated: {context.User.Identity.Name}"
                : "User NOT authenticated");

            await next(context);

            Console.WriteLine($"Response status: {context.Response.StatusCode}");
            Console.WriteLine("=== END REQUEST ===");
        };
    }

    private static async Task InitializeMongoIndexes(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var mongoService = scope.ServiceProvider.GetRequiredService<MongoDBService>();
        await mongoService.CreateIndexesAsync();
    }
}
