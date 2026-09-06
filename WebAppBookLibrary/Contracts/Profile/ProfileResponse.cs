namespace WebAppBookLibrary.Contracts.Profile;

public sealed record ProfileResponse(
    string Id,
    string DisplayName,
    string Username,
    string Email,
    string Role,
    DateTime CreatedAt,
    DateTime? LastLoginAt);
