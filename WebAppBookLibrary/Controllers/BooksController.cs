using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers
{
    [ApiController]
    [Route("[controller]")]
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

        // GET /books
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetAll()
        {
            var books = await _bookService.GetAllAsync();
            return Ok(new { message = "Books retrieved", data = books });
        }

        // GET /books/{id}
        [HttpGet("{id}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetById(string id)
        {
            var book = await _bookService.GetByIdAsync(id);
            if (book == null)
                return NotFound(new { error = "Book not found." });

            return Ok(new { message = "Book retrieved", data = book });
        }

        // POST /books
        [HttpPost]
        [Authorize(Roles = "Admin,Librarian,admin,librarian")]
        public async Task<IActionResult> Create([FromBody] Book book)
        {
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


        // PUT /books/{id}
        [HttpPut("{id}")]
        [Authorize(Roles = "Admin,Librarian,admin,librarian")]
        public async Task<IActionResult> Update(string id, [FromBody] Book updatedBook)
        {
            updatedBook.Id = id;
            var result = await _bookService.UpdateAsync(updatedBook);

            if (!result.Success)
                return NotFound(new { error = result.Message });

            return Ok(new { message = result.Message });
        }

        // DELETE /books/{id}
        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin,admin")]
        public async Task<IActionResult> Delete(string id)
        {
            var result = await _bookService.DeleteAsync(id);

            if (!result.Success)
                return NotFound(new { error = result.Message });

            return Ok(new { message = result.Message });
        }
    }
}
