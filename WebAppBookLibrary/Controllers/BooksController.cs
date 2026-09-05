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
    public async Task<IActionResult> GetAll([FromQuery] BookQuery query, CancellationToken cancellationToken)
    {
        var page = await _bookService.SearchAsync(query, User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Librarian), cancellationToken);
        return Ok(page);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id, CancellationToken cancellationToken = default)
    {
        if (!ObjectId.TryParse(id, out _))
            return ApiProblemFactory.Result(400, "Invalid book identifier");

        var includeInactive = User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Librarian);
        var book = await _bookService.GetDetailAsync(id, includeInactive, cancellationToken);
        if (book is null)
            return ApiProblemFactory.Result(404, "Book not found");

        return Ok(book);
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
        return CreatedAtAction(nameof(GetById), new { id = result.Book!.Id }, result.Book);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> Update(string id, [FromBody] BookWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (!ObjectId.TryParse(id, out _))
            return ApiProblemFactory.Result(400, "Invalid book identifier");

        var result = await _bookService.UpdateAsync(id, request, DateTime.UtcNow, cancellationToken);
        return result.ErrorCode switch
        {
            "book_not_found" => ApiProblemFactory.Result(404, "Book not found"),
            "isbn_conflict" => BookProblem(409, "ISBN already exists", result.ErrorCode),
            "inventory_conflict" => BookProblem(409, "Total copies cannot be lower than active physical loans", result.ErrorCode),
            _ when !result.Success => BookProblem(400, "Book could not be updated", result.ErrorCode),
            _ => Ok(result.Book)
        };
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

        return NoContent();
    }

    [HttpPatch("{id}/status")]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> ChangeStatus(string id, [FromBody] ChangeBookStatusRequest request, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _))
            return ApiProblemFactory.Result(400, "Invalid book identifier");

        var result = await _bookService.SetActiveAsync(id, request.IsActive, DateTime.UtcNow, cancellationToken);
        return result.Success
            ? Ok(new { message = request.IsActive ? "Book activated" : "Book deactivated" })
            : ApiProblemFactory.Result(404, "Book not found");
    }

    private static ObjectResult BookProblem(int statusCode, string title, string errorCode)
    {
        var result = ApiProblemFactory.Result(statusCode, title);
        if (result.Value is ProblemDetails problem)
            problem.Extensions["code"] = errorCode;
        return result;
    }
}
