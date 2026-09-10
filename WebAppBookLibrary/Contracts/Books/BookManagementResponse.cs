namespace WebAppBookLibrary.Contracts.Books;

// Resource URLs are deliberately absent from ordinary detail and catalog contracts.
public sealed record BookManagementResponse(BookDetailResponse Book, string? DigitalResourceUrl);
