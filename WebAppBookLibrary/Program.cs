using System.Security.Claims;
using System.Text;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WebAppBookLibrary.Data;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            Env.Load();

            var builder = WebApplication.CreateBuilder(args);

            // Read CORS origin from environment variable or use default
            var corsOrigin = GetEnvironmentVariable("CORS_ORIGIN", "http://localhost:4200");

            var MyAllowSpecificOrigins = "_myAllowSpecificOrigins";
            builder.Services.AddCors(options =>
            {
                options.AddPolicy(name: MyAllowSpecificOrigins,
                    policy =>
                    {
                        policy.WithOrigins(corsOrigin)
                              .AllowAnyHeader()
                              .AllowAnyMethod();
                    });
            });

            ConfigureJwt(builder.Services, builder.Configuration);
            ConfigureServices(builder.Services);
            ConfigureSwagger(builder.Services);

            var app = builder.Build();

            app.UseCors(MyAllowSpecificOrigins); // ? APLICAR CORS ANTES DE CONTROLLERS

            ConfigurePipeline(app);

            await InitializeMongoIndexes(app);

            await app.RunAsync();
        }

        private static void ConfigurePipeline(WebApplication app)
        {
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();
            app.UseAuthentication();

            if (app.Environment.IsDevelopment())
            {
                app.Use(CreateDevelopmentLoggingMiddleware());
            }

            app.UseAuthorization();
            app.UseExceptionHandler(CreateExceptionHandler());
            app.MapControllers();
        }

        private static Func<HttpContext, RequestDelegate, Task> CreateDevelopmentLoggingMiddleware()
        {
            return async (context, next) =>
            {
                LogRequestStart(context);
                LogAuthenticationInfo(context);

                await next(context);

                Console.WriteLine($"Response status: {context.Response.StatusCode}");
                Console.WriteLine("=== END REQUEST ===");
            };
        }

        private static void LogRequestStart(HttpContext context)
        {
            Console.WriteLine($"=== REQUEST: {context.Request.Method} {context.Request.Path} ===");

            if (context.Request.Headers.ContainsKey("Authorization"))
                Console.WriteLine("Authorization header present");
            else
                Console.WriteLine("NO Authorization header found!");
        }

        private static void LogAuthenticationInfo(HttpContext context)
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                Console.WriteLine($"User authenticated: {context.User.Identity.Name}");
                LogUserClaims(context.User.Claims);
            }
            else
            {
                Console.WriteLine("User NOT authenticated");
            }
        }

        private static void LogUserClaims(IEnumerable<Claim>? claims)
        {
            var claimsList = claims?.ToList() ?? new List<Claim>();
            Console.WriteLine($"Claims count: {claimsList.Count}");

            foreach (var claim in claimsList)
            {
                Console.WriteLine($"  {claim.Type}: {claim.Value}");
            }
        }

        private static Action<IApplicationBuilder> CreateExceptionHandler()
        {
            return errorApp =>
            {
                errorApp.Run(async context =>
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";

                    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                    if (error != null)
                    {
                        Console.WriteLine($"ERROR ALV: {error.Error}");

                        await context.Response.WriteAsync(
                            System.Text.Json.JsonSerializer.Serialize(new
                            {
                                error = "Ha ocurrido un error interno en el servidor.",
                                detail = error.Error.Message
                            }));
                    }
                });
            };
        }

        private static void ConfigureJwt(IServiceCollection services, IConfiguration configuration)
        {
            var jwtSettings = GetJwtSettings(configuration);

            services.AddAuthentication(ConfigureAuthenticationOptions)
                   .AddJwtBearer(options => ConfigureJwtBearerOptions(options, jwtSettings));
        }

        private static JwtSettings GetJwtSettings(IConfiguration configuration)
        {
            // First try environment variables, then fall back to appsettings
            var key = GetEnvironmentVariable("JWT_KEY") ?? configuration.GetSection("Jwt").GetValue<string>("Key");
            var issuer = GetEnvironmentVariable("JWT_ISSUER") ?? configuration.GetSection("Jwt").GetValue<string>("Issuer");
            var audience = GetEnvironmentVariable("JWT_AUDIENCE") ?? configuration.GetSection("Jwt").GetValue<string>("Audience");

            return new JwtSettings
            {
                Key = key ?? throw new InvalidOperationException("JWT Key is missing. Set JWT_KEY environment variable or configure in appsettings.json"),
                Issuer = issuer ?? throw new InvalidOperationException("JWT Issuer is missing. Set JWT_ISSUER environment variable or configure in appsettings.json"),
                Audience = audience ?? throw new InvalidOperationException("JWT Audience is missing. Set JWT_AUDIENCE environment variable or configure in appsettings.json")
            };
        }

        private static string? GetEnvironmentVariable(string variableName, string? defaultValue = null)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }

        private static void ConfigureAuthenticationOptions(AuthenticationOptions options)
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        }

        private static void ConfigureJwtBearerOptions(JwtBearerOptions options, JwtSettings jwtSettings)
        {
            options.TokenValidationParameters = CreateTokenValidationParameters(jwtSettings);
            options.Events = CreateJwtBearerEvents();
        }

        private static TokenValidationParameters CreateTokenValidationParameters(JwtSettings jwtSettings)
        {
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key)),
                RoleClaimType = ClaimTypes.Role,
                NameClaimType = ClaimTypes.Name
            };
        }

        private static JwtBearerEvents CreateJwtBearerEvents()
        {
            return new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    Console.WriteLine($"JWT Authentication failed: {context.Exception}");
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    Console.WriteLine("JWT Token validated successfully");
                    LogValidatedTokenClaims(context.Principal?.Claims);
                    return Task.CompletedTask;
                }
            };
        }

        private static void LogValidatedTokenClaims(IEnumerable<Claim>? claims)
        {
            var claimsList = claims?.ToList();
            if (claimsList != null)
            {
                foreach (var claim in claimsList)
                {
                    Console.WriteLine($"Claim: {claim.Type} = {claim.Value}");
                }
            }
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseInMemoryDatabase("LogDb"));

            services.AddSingleton<MongoDBService>();
            services.AddScoped<IUserStore, MongoUserStore>();
            services.AddScoped<UserService>();
            services.AddScoped<BookService>();
            services.AddScoped<LoanService>();
            services.AddScoped<Logservice>();

            services.AddHttpContextAccessor();
            services.AddAuthorization();
        }

        private static void ConfigureSwagger(IServiceCollection services)
        {
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();
        }

        private static async Task InitializeMongoIndexes(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var mongoService = scope.ServiceProvider.GetRequiredService<MongoDBService>();
            await mongoService.CreateIndexesAsync();
        }
    }

    // Clase auxiliar para encapsular la configuraci�n JWT
    public class JwtSettings
    {
        public required string Key { get; init; }
        public required string Issuer { get; init; }
        public required string Audience { get; init; }
    }
}
