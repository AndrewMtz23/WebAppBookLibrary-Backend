namespace WebAppBookLibrary.Services;
public sealed class CategoryReferenceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
