using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
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

    public BooksController(BookService bookService, Logservice logService)
    {
        _bookService = bookService;
        _logService = logService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery(Name = "")] BookQuery query, CancellationToken cancellationToken)
    {
        var page = await _bookService.SearchAsync(query, User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Librarian), User.Identity?.Name, cancellationToken);
        return Ok(page);
    }

    [HttpGet("facets")]
    public async Task<IActionResult> GetFacets(CancellationToken cancellationToken)
    {
        return Ok(await _bookService.GetGenreFacetsAsync(cancellationToken));
    }

    [HttpGet("{id}")]
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
        return book is null ? ApiProblemFactory.Result(404, "Book not found") : Ok(book);
    }

    [HttpDelete("{id}/permanent")]
    [Authorize(Policy = PolicyNames.DeleteBooks)]
    public async Task<IActionResult> DeletePermanently(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _)) return ApiProblemFactory.Result(400, "Invalid book identifier");
        var result = await _bookService.DeletePermanentlyAsync(id, cancellationToken);
        if (!result.Success)
            return result.ErrorCode == "book_not_found" ? ApiProblemFactory.Result(404, "Book not found") : BookProblem(409, "Book cannot be permanently deleted", result.ErrorCode);
        if (_logService is not null) await _logService.BookChangedAsync("permanently_deleted", User.Identity?.Name ?? string.Empty, id);
        return NoContent();
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> Create([FromBody] BookWriteRequest request, CancellationToken cancellationToken)
    {
        var result = await _bookService.CreateAsync(request, ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, cancellationToken);
        if (!result.Success)
        {
            return result.ErrorCode == "isbn_conflict"
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

        var result = await _bookService.UpdateAsync(id, request, DateTime.UtcNow, cancellationToken);
        var response = result.ErrorCode switch
        {
            "book_not_found" => ApiProblemFactory.Result(404, "Book not found"),
            "isbn_conflict" => BookProblem(409, "ISBN already exists", result.ErrorCode),
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
            return ApiProblemFactory.Result(404, "Book not found");
        if (_logService is not null) await _logService.BookChangedAsync("deactivated", User.Identity?.Name ?? string.Empty, id);
        return NoContent();
    }

    [HttpPatch("{id}/status")]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> ChangeStatus(string id, [FromBody] ChangeBookStatusRequest request, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _))
            return ApiProblemFactory.Result(400, "Invalid book identifier");

        var result = await _bookService.SetActiveAsync(id, request.IsActive, DateTime.UtcNow, cancellationToken);
        if (!result.Success) return ApiProblemFactory.Result(404, "Book not found");
        if (_logService is not null) await _logService.BookChangedAsync(request.IsActive ? "activated" : "deactivated", User.Identity?.Name ?? string.Empty, id);
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
