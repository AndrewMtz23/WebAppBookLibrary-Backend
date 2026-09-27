using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebAppBookLibrary.Configuration;

namespace WebAppBookLibrary.Tests;

public sealed class AccountRecoveryConfigurationTests
{
    [Theory]
    [InlineData("https://library.example.invalid", true)]
    [InlineData("http://localhost:4284", true)]
    [InlineData("http://library.example.invalid", false)]
    [InlineData("https://user:secret@library.example.invalid", false)]
    [InlineData("https://library.example.invalid?next=elsewhere", false)]
    [InlineData("https://library.example.invalid#token=oops", false)]
    public void Enabled_recovery_requires_trusted_url(string url, bool valid)
    {
        using var services = Services(new Dictionary<string, string?> {
            ["AccountRecovery:Enabled"] = "true", ["AccountRecovery:PublicBaseUrl"] = url,
            ["AccountRecovery:LocalMailDirectory"] = Path.GetTempPath()
        });
        if (valid) Assert.True(services.GetRequiredService<IOptions<AccountRecoveryOptions>>().Value.Enabled);
        else Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<AccountRecoveryOptions>>().Value);
    }

    [Fact]
    public void Default_is_disabled_and_relative_mailbox_is_rejected_when_enabled()
    {
        using var defaults = Services([]);
        Assert.False(defaults.GetRequiredService<IOptions<AccountRecoveryOptions>>().Value.Enabled);
        using var invalid = Services(new Dictionary<string, string?> { ["AccountRecovery:Enabled"] = "true", ["AccountRecovery:LocalMailDirectory"] = "relative" });
        Assert.Throws<OptionsValidationException>(() => invalid.GetRequiredService<IOptions<AccountRecoveryOptions>>().Value);
    }

    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        typeof(Program).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        return services.BuildServiceProvider();
    }
}
