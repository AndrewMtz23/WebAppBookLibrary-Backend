namespace WebAppBookLibrary.Services;

public sealed class BookReferenceUnavailableException : Exception
{
    public BookReferenceUnavailableException() : base("Book is no longer available for this reference.") { }
}
