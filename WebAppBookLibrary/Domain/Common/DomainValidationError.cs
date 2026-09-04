namespace WebAppBookLibrary.Domain.Common;

public sealed record DomainValidationError(string Code, string Field, string Message);
