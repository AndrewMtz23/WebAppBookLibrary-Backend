using System.Security.Claims;
using WebAppBookLibrary.Services.Branches;
using WebAppBookLibrary.Services.Books;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BooksController : ControllerBase
{
    private readonly BookService _bookService;
    private readonly Logservice _logService;

    private readonly MongoDBService? _mongo;
    private readonly BookChangeService? _changes;
    private readonly BranchRequestContext? _context;

    public BooksController(BookService bookService, Logservice logService, MongoDBService? mongo = null, BookChangeService? changes = null, BranchRequestContext? context = null)
    {
        _bookService = bookService;
        _mongo = mongo; _changes = changes; _context = context;
        _logService = logService;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll([FromQuery(Name = "")] BookQuery query, CancellationToken cancellationToken)
    {
        try
        {
            if (query.CategoryId is not null && !ObjectId.TryParse(query.CategoryId, out _)) return BookProblem(400, "Invalid category identifier", "category_ids_invalid");
            var page = await _bookService.SearchAsync(query, User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Librarian), User.Identity?.Name, cancellationToken);
            return Ok(page);
        }
        catch (CategoryReferenceException e) { return BookProblem(400, "Category filters disagree", e.Code); }
    }

    [HttpGet("facets")]
    [AllowAnonymous]
    public async Task<IActionResult> GetFacets(CancellationToken cancellationToken)
    {
        return Ok(await _bookService.GetGenreFacetsAsync(cancellationToken));
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(string id, CancellationToken cancellationToken = default)
    {
        if (!ObjectId.TryParse(id, out _))
            return ApiProblemFactory.Result(400, "Invalid book identifier");

        var includeInactive = User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Librarian);
        var book = await _bookService.GetDetailAsync(id, includeInactive, User.Identity?.Name, cancellationToken);
        if (book is null)
            return ApiProblemFactory.Result(404, "Book not found");

        return Ok(book);
    }

    [HttpGet("{id}/management")]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> GetManagement(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return ApiProblemFactory.Result(400, "Invalid book identifier");
        var book = await _bookService.GetManagementAsync(id, cancellationToken);
        if (book is not null && _mongo is not null && await BranchRequestContext.EnabledAsync(_mongo._database, cancellationToken))
        {
            var branch = await BranchRequestContext.ResolveAsync(_mongo, _context, cancellationToken);
            var catalog = _mongo._database.GetCollection<BranchCatalogEntry>("BranchCatalog");
            var entry = await catalog.Find(e => e.BranchId == branch && e.BookId == id).FirstOrDefaultAsync(cancellationToken);
            book = book with { CatalogVersion = entry?.Version, CatalogActive = entry?.IsActive,
                RequiresApproval = !User.IsInRole("admin") && await catalog.CountDocumentsAsync(e => e.BookId == id, cancellationToken: cancellationToken) > 1 };
        }
        return book is null ? ApiProblemFactory.Result(404, "Book not found") : Ok(book);
    }

    [HttpDelete("{id}/permanent")]
    [Authorize(Policy = PolicyNames.DeleteBooks)]
    public async Task<IActionResult> DeletePermanently(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return ApiProblemFactory.Result(400, "Invalid book identifier");
        var result = await _bookService.DeletePermanentlyAsync(id, cancellationToken);
        if (!result.Success)
        {
            if (_logService is not null) await _logService.BookChangedAsync("permanent_delete_attempt", User.Identity?.Name ?? string.Empty, id, new Dictionary<string, string> { ["result"] = "failed", ["reasonCode"] = result.ErrorCode });
            return result.ErrorCode == "book_not_found" ? ApiProblemFactory.Result(404, "Book not found") : BookProblem(409, "Book cannot be permanently deleted", result.ErrorCode);
        }
        if (_logService is not null) await _logService.BookChangedAsync("permanently_deleted", User.Identity?.Name ?? string.Empty, id, new Dictionary<string, string> { ["result"] = "success" });
        return NoContent();
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> Create([FromBody] BookWriteRequest request, CancellationToken cancellationToken)
    {
        if (_mongo is not null && await BranchRequestContext.EnabledAsync(_mongo._database, cancellationToken))
        {
            var branch = await BranchRequestContext.ResolveAsync(_mongo, _context, cancellationToken);
            var created = await _changes!.CreateAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", branch!, request, cancellationToken);
            return created.Success ? Ok(WebAppBookLibrary.Domain.Books.BookMapper.ToDetail(created.Value!, 0, false)) : BookProblem(created.ErrorCode == "branch_forbidden" ? 403 : 409, "No se pudo crear el libro", created.ErrorCode!);
        }
        var result = await _bookService.CreateAsync(request, ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, cancellationToken);
        if (!result.Success)
        {
            return result.ErrorCode is "isbn_conflict" or "category_unavailable" or "category_inactive"
                ? BookProblem(409, "ISBN already exists", result.ErrorCode)
                : BookProblem(400, "Book could not be created", result.ErrorCode);
        }
        if (_logService is not null) await _logService.BookChangedAsync("created", User.Identity?.Name ?? string.Empty, result.Book!.Id, new Dictionary<string, string> { ["mediaType"] = result.Book.MediaType });
        return CreatedAtAction(nameof(GetById), new { id = result.Book!.Id }, result.Book);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> Update(string id, [FromBody] BookWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (!ObjectId.TryParse(id, out _))
            return ApiProblemFactory.Result(400, "Invalid book identifier");

        if (_mongo is not null && await BranchRequestContext.EnabledAsync(_mongo._database, cancellationToken))
        {
            var branch = await BranchRequestContext.ResolveAsync(_mongo, _context, cancellationToken);
            var edited = await _changes!.EditAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", branch!, id, request, request.ExpectedMetadataVersion ?? 0, cancellationToken);
            return edited.Success ? Ok(WebAppBookLibrary.Domain.Books.BookMapper.ToDetail(edited.Value!, 0, false)) : BookProblem(edited.ErrorCode == "branch_forbidden" ? 403 : 409, "No se pudo modificar el libro", edited.ErrorCode!);
        }
        var result = await _bookService.UpdateAsync(id, request, DateTime.UtcNow, cancellationToken);
        var response = result.ErrorCode switch
        {
            "book_not_found" => ApiProblemFactory.Result(404, "Book not found"),
            "isbn_conflict" => BookProblem(409, "ISBN already exists", result.ErrorCode),
            "category_unavailable" or "category_inactive" => BookProblem(409, "Category is no longer available", result.ErrorCode),
            "inventory_conflict" => BookProblem(409, "Total copies cannot be lower than active physical loans", result.ErrorCode),
            "concurrent_update_conflict" => BookProblem(409, "Book changed while it was being updated", result.ErrorCode),
            _ when !result.Success => BookProblem(400, "Book could not be updated", result.ErrorCode),
            _ => Ok(result.Book)
        };
        if (result.Success && _logService is not null) await _logService.BookChangedAsync("updated", User.Identity?.Name ?? string.Empty, id, new Dictionary<string, string> { ["mediaType"] = result.Book!.MediaType });
        return response;
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = PolicyNames.DeleteBooks)]
    public async Task<IActionResult> Delete(string id)
    {
        if (!ObjectId.TryParse(id, out _))
            return ApiProblemFactory.Result(400, "Invalid book identifier");

        var result = await _bookService.DeleteAsync(id);
        if (!result.Success)
        {
            if (_logService is not null) await _logService.BookChangedAsync("deactivation_attempt", User.Identity?.Name ?? string.Empty, id, new Dictionary<string, string> { ["result"] = "failed", ["reasonCode"] = "book_not_found" });
            return ApiProblemFactory.Result(404, "Book not found");
        }
        if (_logService is not null) await _logService.BookChangedAsync("deactivated", User.Identity?.Name ?? string.Empty, id, new Dictionary<string, string> { ["result"] = "success" });
        return NoContent();
    }

    [HttpPatch("{id}/status")]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> ChangeStatus(string id, [FromBody] ChangeBookStatusRequest request, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _))
            return ApiProblemFactory.Result(400, "Invalid book identifier");

        if (_mongo is not null && await BranchRequestContext.EnabledAsync(_mongo._database, cancellationToken))
        {
            var branch = await BranchRequestContext.ResolveAsync(_mongo, _context, cancellationToken);
            if (request.ExpectedCatalogVersion is null) return BookProblem(409, "Se requiere la versión del catálogo", "catalog_version_required");
            var changed = await new BranchCatalogStore(_mongo, TimeProvider.System).SetAsync(_context?.ActorId ?? "", branch!, id, request.IsActive, request.ExpectedCatalogVersion.Value, cancellationToken);
            return changed.Success ? Ok(changed.Value) : BookProblem(changed.ErrorCode == "branch_forbidden" ? 403 : 409, "No se pudo actualizar el catálogo", changed.ErrorCode!);
        }
        var result = await _bookService.SetActiveAsync(id, request.IsActive, DateTime.UtcNow, cancellationToken);
        if (!result.Success)
        {
            if (_logService is not null) await _logService.BookChangedAsync("status_change_attempt", User.Identity?.Name ?? string.Empty, id, new Dictionary<string, string> { ["result"] = "failed", ["reasonCode"] = "book_not_found", ["status"] = request.IsActive ? "active" : "inactive" });
            return ApiProblemFactory.Result(404, "Book not found");
        }
        if (_logService is not null) await _logService.BookChangedAsync(request.IsActive ? "activated" : "deactivated", User.Identity?.Name ?? string.Empty, id, new Dictionary<string, string> { ["result"] = "success", ["status"] = request.IsActive ? "active" : "inactive" });
        return Ok(new { message = request.IsActive ? "Book activated" : "Book deactivated" });
    }

    private static ObjectResult BookProblem(int statusCode, string title, string errorCode)
    {
        var result = ApiProblemFactory.Result(statusCode, title);
        if (result.Value is ProblemDetails problem)
            problem.Extensions["code"] = errorCode;
        return result;
    }
}
