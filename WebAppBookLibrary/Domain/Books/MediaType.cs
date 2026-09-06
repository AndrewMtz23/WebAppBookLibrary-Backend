namespace WebAppBookLibrary.Domain.Books;

public static class MediaTypes
{
    public const string Physical = "physical";
    public const string Digital = "digital";

    public static bool IsCanonical(string? value) =>
        value is Physical or Digital;
}
