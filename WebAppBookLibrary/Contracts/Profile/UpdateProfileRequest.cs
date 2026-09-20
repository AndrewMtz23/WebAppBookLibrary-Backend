using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebAppBookLibrary.Contracts.Profile;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProfileRequest(
    [Required, StringLength(120, MinimumLength = 1)] string DisplayName,
    [Required, StringLength(254), EmailAddress] string Email,
    [StringLength(2048)] string? AvatarUrl,
    DateTime ExpectedUpdatedAt);
