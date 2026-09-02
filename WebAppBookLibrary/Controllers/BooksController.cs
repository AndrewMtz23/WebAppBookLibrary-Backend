using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using WebAppBookLibrary.Contracts.Books;
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
    public async Task<IActionResult> GetAll()
    {
        var books = await _bookService.GetAllAsync();
        return Ok(new { message = "Books retrieved", data = books });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var book = await _bookService.GetByIdAsync(id);
        if (book is null)
            return NotFound(new { error = "Book not found." });

        return Ok(new { message = "Book retrieved", data = book });
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> Create([FromBody] UpsertBookRequest request)
    {
        var book = MapBook(request, ObjectId.GenerateNewId().ToString(), isAvailable: true);
        await _logService.LogAsync("INFORMATION", $"Intentando registrar nuevo libro: {book.Title}");

        var result = await _bookService.CreateAsync(book);
        if (!result.Success)
        {
            await _logService.LogAsync("WARNING", $"Error al registrar libro: {result.Message}");
            return BadRequest(new { error = result.Message });
        }

        await _logService.LogAsync("INFORMATION", $"Libro registrado exitosamente: {result.Book!.Title}");

        return CreatedAtAction(nameof(GetById), new { id = result.Book.Id }, new
        {
            message = result.Message,
            data = result.Book
        });
    }

    [HttpPut("{id}")]
    [Authorize(Policy = PolicyNames.ManageBooks)]
    public async Task<IActionResult> Update(string id, [FromBody] UpsertBookRequest request)
    {
        var existingBook = await _bookService.GetByIdAsync(id);
        if (existingBook is null)
            return NotFound(new { error = "Book not found." });

        var updatedBook = MapBook(request, id, existingBook.IsAvailable);
        var result = await _bookService.UpdateAsync(updatedBook);
        if (!result.Success)
            return NotFound(new { error = result.Message });

        return Ok(new { message = result.Message });
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = PolicyNames.DeleteBooks)]
    public async Task<IActionResult> Delete(string id)
    {
        var result = await _bookService.DeleteAsync(id);
        if (!result.Success)
            return NotFound(new { error = result.Message });

        return Ok(new { message = result.Message });
    }

    private static Book MapBook(UpsertBookRequest request, string id, bool isAvailable)
    {
        return new Book
        {
            Id = id,
            Title = request.Title,
            Author = request.Author,
            Year = request.Year,
            Genre = request.Genre,
            IsAvailable = isAvailable
        };
    }
}
