using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;
using MongoDB.Driver;

namespace WebAppBookLibrary.Services;

public sealed class BookService
{
    private readonly IBookStore _store;
    private readonly Logservice? _log;
    public BookService(IBookStore store) => _store = store;
    public BookService(IBookStore store, Logservice log) : this(store) => _log = log;

    public async Task<PagedResult<BookSummaryResponse>> SearchAsync(BookQuery query, bool includeInactive, string? viewerUsername, CancellationToken token)
    {
        var page = await _store.SearchAsync(query.Normalize(), includeInactive, viewerUsername, token);
        return new(page.Items.Select(item => BookMapper.ToSummary(item.Book, item.ReservationCount, item.IsFavorite)).ToArray(), page.Page, page.PageSize, page.TotalItems);
    }

    public Task<PagedResult<BookSummaryResponse>> SearchAsync(BookQuery query, bool includeInactive, CancellationToken token) => SearchAsync(query, includeInactive, null, token);
    public async Task<List<Book>> GetAllAsync() => (await _store.SearchAsync(new BookQuery { PageSize = 100 }.Normalize(), true, null, CancellationToken.None)).Items.Select(item => item.Book).ToList();
    public Task<Book?> GetByIdAsync(string id) => _store.FindByIdAsync(id, CancellationToken.None);

    public async Task<BookDetailResponse?> GetDetailAsync(string id, bool includeInactive, string? viewerUsername, CancellationToken token)
    {
        var entry = await _store.FindCatalogEntryAsync(id, includeInactive, viewerUsername, token);
        return entry is null ? null : BookMapper.ToDetail(entry.Book, entry.ReservationCount, entry.IsFavorite);
    }
    public Task<BookDetailResponse?> GetDetailAsync(string id, bool includeInactive, CancellationToken token) => GetDetailAsync(id, includeInactive, null, token);

    public async Task<BookMutationResult> SetActiveAsync(string id, bool isActive, DateTime updatedAtUtc, CancellationToken token)
    {
        var changed = await _store.SetActiveAsync(id, isActive, updatedAtUtc, token);
        return changed ? new(true, string.Empty) : new(false, "book_not_found");
    }

    public async Task<BookWriteResult> CreateAsync(BookWriteRequest request, string id, DateTime nowUtc, CancellationToken token)
    {
        var book = BookMapper.ToNewEntity(request, id, nowUtc);
        if (book.Isbn is not null && await _store.IsbnExistsAsync(book.Isbn, null, token))
            return new(false, "isbn_conflict", null);

        try
        {
            await _store.InsertAsync(book, token);
            await LogAsync("INFORMATION", $"Book created: {book.Id}");
            return new(true, string.Empty, BookMapper.ToDetail(book, 0, false));
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(false, "isbn_conflict", null);
        }
    }

    public async Task<BookWriteResult> UpdateAsync(string id, BookWriteRequest request, DateTime nowUtc, CancellationToken token)
    {
        var existing = await _store.FindByIdAsync(id, token);
        if (existing is null)
            return new(false, "book_not_found", null);

        var activePhysicalLoans = await _store.CountActivePhysicalLoansAsync(id, token);
        if (activePhysicalLoans > 0 && existing.MediaType != request.MediaType)
            return new(false, "inventory_conflict", null);
        if (request.MediaType == MediaTypes.Physical && request.TotalCopies < activePhysicalLoans)
            return new(false, "inventory_conflict", null);

        var normalizedIsbn = BookRules.NormalizeIsbn(request.Isbn);
        if (normalizedIsbn is not null && await _store.IsbnExistsAsync(normalizedIsbn, id, token))
            return new(false, "isbn_conflict", null);

        var book = BookMapper.ToUpdatedEntity(request, existing, activePhysicalLoans, nowUtc);
        try
        {
            if (!await _store.ReplaceMetadataAsync(book, existing.UpdatedAt, token))
                return new(false, "concurrent_update_conflict", null);
            await LogAsync("INFORMATION", $"Book updated: {book.Id}");
            return new(true, string.Empty, BookMapper.ToDetail(book, 0, false));
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(false, "isbn_conflict", null);
        }
    }

    public async Task<(bool Success, string Message, Book? Book)> CreateAsync(Book book)
    {
        if (string.IsNullOrWhiteSpace(book.Title) || string.IsNullOrWhiteSpace(book.Author)) return (false, "Title and Author are required.", null);
        try { await _store.InsertAsync(book, CancellationToken.None); await LogAsync("INFORMATION", $"Book created: {book.Title}"); return (true, "Book created successfully.", book); }
        catch (Exception exception) { await LogAsync("ERROR", "Error creating book.", exception); return (false, "Error creating book.", null); }
    }

    public async Task<(bool Success, string Message)> UpdateAsync(Book book)
    {
        try { return await _store.ReplaceMetadataAsync(book, book.UpdatedAt, CancellationToken.None) ? (true, "Book updated successfully.") : (false, "Book not found."); }
        catch (Exception exception) { await LogAsync("ERROR", "Error updating book.", exception); return (false, "Error updating book."); }
    }

    public async Task<(bool Success, string Message)> DeleteAsync(string id)
    {
        try { return await _store.SetActiveAsync(id, false, DateTime.UtcNow, CancellationToken.None) ? (true, "Book deactivated successfully.") : (false, "Book not found."); }
        catch (Exception exception) { await LogAsync("ERROR", "Error deleting book.", exception); return (false, "Error deleting book."); }
    }

    private Task LogAsync(string level, string message, Exception? exception = null) => _log?.LogAsync(level, message, exception) ?? Task.CompletedTask;
}

public sealed record BookMutationResult(bool Success, string ErrorCode);
public sealed record BookWriteResult(bool Success, string ErrorCode, BookDetailResponse? Book);
