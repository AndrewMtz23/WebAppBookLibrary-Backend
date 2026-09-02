using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WebAppBookLibrary.Configuration;

namespace WebAppBookLibrary.Tests;

public class JwtOptionsTests
{
    [Fact]
    public void JwtOptions_rejects_short_signing_key()
    {
        var options = new JwtOptions
        {
            Key = "short",
            Issuer = "issuer",
            Audience = "audience"
        };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(JwtOptions.Key)));
    }

    [Fact]
    public void JwtOptions_accepts_complete_settings_with_strong_signing_key()
    {
        var options = new JwtOptions
        {
            Key = "a-signing-key-with-at-least-32-characters",
            Issuer = "issuer",
            Audience = "audience"
        };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public void JwtOptions_rejects_known_placeholder_signing_key()
    {
        var options = new JwtOptions
        {
            Key = "default-development-key-change-in-production-with-env-var",
            Issuer = "issuer",
            Audience = "audience"
        };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(JwtOptions.Key)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("default-development-key-change-in-production-with-env-var")]
    public async Task Startup_rejects_missing_short_or_placeholder_signing_key(string? signingKey)
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{JwtOptions.SectionName}:Key"] = signingKey,
                    [$"{JwtOptions.SectionName}:Issuer"] = "issuer",
                    [$"{JwtOptions.SectionName}:Audience"] = "audience"
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddOptions<JwtOptions>()
                    .Bind(context.Configuration.GetSection(JwtOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
            })
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public void Committed_settings_do_not_supply_a_signing_key()
    {
        var settingsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "WebAppBookLibrary",
            "appsettings.json"));
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(settingsPath)
            .Build();

        Assert.Null(configuration[$"{JwtOptions.SectionName}:Key"]);
    }
}
