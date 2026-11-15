using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Services.Books;

public sealed class BookChangeService(MongoDBService mongo, TimeProvider clock)
{
    private IMongoDatabase Db => mongo._database;
    private IMongoCollection<BranchCatalogEntry> Catalog => Db.GetCollection<BranchCatalogEntry>("BranchCatalog");
    private IMongoCollection<BookChangeRequest> Requests => Db.GetCollection<BookChangeRequest>("BookChangeRequests");
    private DateTime Now => new BsonDateTime(clock.GetUtcNow().UtcDateTime).ToUniversalTime();

    private async Task<BranchResult<T>> Transact<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try
        {
            using var session = await Db.Client.StartSessionAsync(cancellationToken: ct);
            return new(true, null, await session.WithTransactionAsync(action, new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct));
        }
        catch (BranchAccessException ex) { return new(false, ex.Code); }
        catch (CategoryReferenceException ex) { return new(false, ex.Code); }
        catch (UserReferenceUnavailableException) { return new(false, "branch_forbidden"); }
        catch (MongoException ex) when (ex is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } or MongoCommandException { Code: 11000 }) { return new(false, "isbn_conflict"); }
    }

    private async Task<Book> Prepare(BookWriteRequest request, Book? previous, CancellationToken ct)
    {
        if (request.Validate(new ValidationContext(request)).Any()) throw new BranchAccessException("book_invalid");
        if (previous is not null && (request.MediaType != previous.MediaType || request.TotalCopies != previous.TotalCopies))
            throw new BranchAccessException("book_inventory_not_metadata");
        var book = BookMapper.ToNewEntity(request, previous?.Id ?? ObjectId.GenerateNewId().ToString(), Now);
        await new CategoryService(mongo).ResolveWriteAsync(book, request, previous, ct);
        return book;
    }

    private async Task LockCategories(IClientSessionHandle tx, Book proposed, Book? previous, CancellationToken ct)
    {
        foreach (var id in proposed.CategoryIds.Order(StringComparer.Ordinal))
        {
            var filter = Builders<Category>.Filter.Eq(c => c.Id, id);
            if (previous?.CategoryIds.Contains(id) != true) filter &= Builders<Category>.Filter.Eq(c => c.IsActive, true);
            if ((await mongo.Categories.UpdateOneAsync(tx, filter, Builders<Category>.Update.Inc(c => c.ReferenceVersion, 1), cancellationToken: ct)).MatchedCount != 1)
                throw new BranchAccessException("category_unavailable");
        }
    }

    private async Task<Book> LockBook(IClientSessionHandle tx, string bookId, string branchId, long version, CancellationToken ct)
    {
        if (!ObjectId.TryParse(bookId, out _)) throw new BranchAccessException("book_not_found");
        var book = await mongo.Books.FindOneAndUpdateAsync(tx, b => b.Id == bookId, Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1), new() { ReturnDocument = ReturnDocument.After }, ct)
            ?? throw new BranchAccessException("book_not_found");
        if (!await Catalog.Find(tx, e => e.BookId == bookId && e.BranchId == branchId && e.IsActive).AnyAsync(ct)) throw new BranchAccessException("book_not_found");
        if (book.MetadataVersion != version) throw new BranchAccessException("book_version_conflict");
        return book;
    }

    public Task<BranchResult<Book>> CreateAsync(string actor, string branchId, BookWriteRequest request, CancellationToken ct) => Transact(async (tx, token) =>
    {
        await new BranchAccessService(mongo).ValidateForMutationAsync(tx, actor, branchId, token);
        var book = await Prepare(request, null, token);
        await LockCategories(tx, book, null, token);
        // Newly created titles start with individually managed inventory; existing aggregate titles migrate unchanged.
        if (book.MediaType == "physical" && book.TotalCopies != 0) throw new BranchAccessException("book_create_copies_separately");
        if (book.MediaType == "physical") book.InventoryMode = "unit";
        book.InventoryBranchId = book.MediaType == "physical" ? branchId : null;
        await mongo.Books.InsertOneAsync(tx, book, cancellationToken: token);
        await Catalog.InsertOneAsync(tx, new() { Id = branchId + ":" + book.Id, BranchId = branchId, BookId = book.Id, UpdatedAt = Now }, cancellationToken: token);
        await History(tx, book, actor, branchId, "created", null, token);
        return book;
    }, ct);

    public Task<BranchResult<Book>> EditAsync(string actor, string branchId, string bookId, BookWriteRequest request, long version, CancellationToken ct) => Transact(async (tx, token) =>
    {
        var access = await new BranchAccessService(mongo).ValidateForMutationAsync(tx, actor, branchId, token);
        var current = await LockBook(tx, bookId, branchId, version, token);
        // Historical membership also prevents withdrawal from silently bypassing shared metadata review.
        if (!access.IsAdmin && await Catalog.CountDocumentsAsync(tx, e => e.BookId == bookId, cancellationToken: token) > 1)
            throw new BranchAccessException("book_approval_required");
        var proposed = await Prepare(request, current, token);
        return await Apply(tx, current, proposed, actor, branchId, null, token);
    }, ct);

    public Task<BranchResult<BookChangeRequest>> ProposeAsync(string actor, string branchId, string bookId, BookWriteRequest request, long version, CancellationToken ct) => Transact(async (tx, token) =>
    {
        await new BranchAccessService(mongo).ValidateForMutationAsync(tx, actor, branchId, token);
        var current = await LockBook(tx, bookId, branchId, version, token);
        var proposed = await Prepare(request, current, token);
        await LockCategories(tx, proposed, current, token);
        var change = new BookChangeRequest { Id = ObjectId.GenerateNewId().ToString(), BookId = bookId, BranchId = branchId, RequestedBy = actor, BaseVersion = version, Original = current, Proposed = proposed, CreatedAt = Now };
        await Requests.InsertOneAsync(tx, change, cancellationToken: token);
        await AuditDecision(tx, actor, change, "book_change_requested", token);
        return change;
    }, ct);

    public Task<BranchResult<BookChangeRequest>> DecideAsync(string actor, string requestId, bool approve, long version, CancellationToken ct) => Transact(async (tx, token) =>
    {
        if (!ObjectId.TryParse(actor, out _) || !await mongo.Users.Find(tx, u => u.Id == actor && u.IsActive && u.Role == "admin").AnyAsync(token)) throw new BranchAccessException("branch_forbidden");
        await UserReferenceGuard.TouchAsync(mongo.Users, tx, actor, token);
        if (!ObjectId.TryParse(requestId, out _)) throw new BranchAccessException("book_request_not_found");
        var change = await Requests.Find(tx, r => r.Id == requestId).FirstOrDefaultAsync(token) ?? throw new BranchAccessException("book_request_not_found");
        if (change.Status != "pending" || change.Version != version) throw new BranchAccessException("version_conflict");
        if (approve)
        {
            await new BranchAccessService(mongo).ValidateForMutationAsync(tx, actor, change.BranchId, token);
            var current = await LockBook(tx, change.BookId, change.BranchId, change.BaseVersion, token);
            await Apply(tx, current, change.Proposed, actor, change.BranchId, requestId, token);
        }
        change.Status = approve ? "approved" : "rejected"; change.Version++; change.DecidedAt = Now; change.DecidedBy = actor;
        await Requests.ReplaceOneAsync(tx, r => r.Id == requestId, change, cancellationToken: token);
        await AuditDecision(tx, actor, change, "book_change_" + change.Status, token);
        if (await mongo.Users.Find(tx, u => u.Id == change.RequestedBy && u.IsActive).AnyAsync(token))
            await NotificationEvents.AppendEventAsync(Db, tx, change.RequestedBy, "book-change:" + change.Id,
                "book_change_" + change.Status, approve ? "Cambio bibliográfico aprobado" : "Cambio bibliográfico rechazado",
                approve ? "Se aplicaron los cambios solicitados a la ficha compartida." : "La ficha conserva sus datos. Consulta el estado de tu solicitud.",
                change.BookId, null, null, Now, token, branchId: change.BranchId);
        return change;
    }, ct);

    private async Task<Book> Apply(IClientSessionHandle tx, Book current, Book proposed, string actor, string branchId, string? requestId, CancellationToken ct)
    {
        await LockCategories(tx, proposed, current, ct);
        if (proposed.MediaType != current.MediaType) throw new BranchAccessException("book_inventory_not_metadata");
        proposed.CreatedAt = current.CreatedAt; proposed.UpdatedAt = Now; proposed.MetadataVersion = current.MetadataVersion + 1;
        proposed.InventoryMode = current.InventoryMode; proposed.InventoryBranchId = current.InventoryBranchId; proposed.InventoryVersion = current.InventoryVersion;
        proposed.TotalCopies = current.TotalCopies; proposed.AvailableCopies = current.AvailableCopies; proposed.RetainedCopies = current.RetainedCopies;
        proposed.IsActive = current.IsActive; proposed.IsAvailable = current.IsAvailable; proposed.ActiveLoanId = current.ActiveLoanId; proposed.ReferenceVersion = current.ReferenceVersion;
        await mongo.Books.ReplaceOneAsync(tx, b => b.Id == current.Id, proposed, cancellationToken: ct);
        await History(tx, proposed, actor, branchId, requestId is null ? "edited" : "approved", requestId, ct);
        return proposed;
    }

    private Task History(IClientSessionHandle tx, Book book, string actor, string branchId, string action, string? requestId, CancellationToken ct) =>
        Db.GetCollection<BookMetadataHistoryEntry>("BookMetadataHistory").InsertOneAsync(tx, new() { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id, BranchId = branchId, ActorId = actor, Action = action, Version = book.MetadataVersion, RequestId = requestId, CreatedAt = Now, Snapshot = book }, cancellationToken: ct);

    private Task AuditDecision(IClientSessionHandle tx, string actor, BookChangeRequest request, string action, CancellationToken ct) =>
        mongo.LogEntries.InsertOneAsync(tx, new() { Id = ObjectId.GenerateNewId().ToString(), Timestamp = Now, Level = "Information", EventType = action, ActorId = actor, TargetType = "book_change", TargetId = request.Id, Metadata = new() { ["branchId"] = request.BranchId, ["bookId"] = request.BookId } }, cancellationToken: ct);
}
