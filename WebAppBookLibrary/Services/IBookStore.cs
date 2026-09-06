using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed record BookCatalogEntry(Book Book, long ReservationCount, bool IsFavorite);

public interface IBookStore
{
    Task<PagedResult<BookCatalogEntry>> SearchAsync(NormalizedBookQuery query, bool includeInactive, string? viewerUsername, CancellationToken cancellationToken);
    Task<BookCatalogEntry?> FindCatalogEntryAsync(string id, bool includeInactive, string? viewerUsername, CancellationToken cancellationToken);
    Task<Book?> FindByIdAsync(string id, CancellationToken cancellationToken);
    Task<bool> IsbnExistsAsync(string normalizedIsbn, string? excludingId, CancellationToken cancellationToken);
    Task InsertAsync(Book book, CancellationToken cancellationToken);
    Task<int> CountActivePhysicalLoansAsync(string bookId, CancellationToken cancellationToken);
    Task<bool> ReplaceMetadataAsync(Book book, DateTime expectedUpdatedAt, CancellationToken cancellationToken);
    Task<bool> SetActiveAsync(string id, bool isActive, DateTime updatedAtUtc, CancellationToken cancellationToken);
}
