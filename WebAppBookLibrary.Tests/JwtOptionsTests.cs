using System.ComponentModel.DataAnnotations;
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
}
