using WebAppBookLibrary.Domain.Books;

namespace WebAppBookLibrary.Tests;

public sealed class BookRulesTests
{
    [Theory]
    [InlineData("978-0-306-40615-7", "9780306406157")]
    [InlineData(" 84 376 0494 x ", "843760494X")]
    [InlineData("", null)]
    public void NormalizeIsbn_RemovesSeparatorsAndUppercases(string input, string? expected)
    {
        Assert.Equal(expected, BookRules.NormalizeIsbn(input));
    }

    [Fact]
    public void NormalizeList_TrimsRemovesEmptyAndDeduplicatesIgnoringCase()
    {
        var result = BookRules.NormalizeList([" Fantasía ", "fantasía", "", "Historia"]);

        Assert.Equal(["Fantasía", "Historia"], result);
    }

    [Fact]
    public void ValidateMedia_DigitalRejectsInventory()
    {
        var errors = BookRules.ValidateMedia(
            MediaTypes.Digital,
            "https://cdn.example/books/title.pdf",
            totalCopies: 1,
            availableCopies: 1);

        Assert.Contains(errors, error => error.Code == "digital_inventory_not_allowed");
    }

    [Fact]
    public void ValidateMedia_ActiveDigitalRequiresSecureResource()
    {
        var errors = BookRules.ValidateMedia(
            MediaTypes.Digital,
            "http://cdn.example/books/title.pdf",
            totalCopies: null,
            availableCopies: null,
            isActive: true);

        Assert.Contains(errors, error => error.Code == "digital_resource_invalid");
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, 2)]
    public void ValidateMedia_PhysicalRejectsInvalidInventory(int available, int total)
    {
        var errors = BookRules.ValidateMedia(
            MediaTypes.Physical,
            digitalResourceUrl: null,
            totalCopies: total,
            availableCopies: available);

        Assert.Contains(errors, error => error.Code == "physical_inventory_invalid");
    }
}
