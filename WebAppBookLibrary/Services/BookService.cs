using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services
{
    public class BookService
    {
        private readonly IMongoCollection<Book> _books;
        private readonly Logservice _logService;

        public BookService(MongoDBService dbService, Logservice logService)
        {
            _books = dbService.Books;
            _logService = logService;
        }

        public async Task<List<Book>> GetAllAsync()
        {
            return await _books.Find(_ => true).ToListAsync();
        }

        public async Task<Book?> GetByIdAsync(string id)
        {
            return await _books.Find(b => b.Id == id).FirstOrDefaultAsync();
        }

        public async Task<(bool Success, string Message, Book? Book)> CreateAsync(Book book)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(book.Title) || string.IsNullOrWhiteSpace(book.Author))
                    return (false, "Title and Author are required.", null);

                await _books.InsertOneAsync(book);
                await _logService.LogAsync("INFORMATION", $"Book created: {book.Title}");
                return (true, "Book created successfully.", book);
            }
            catch (Exception ex)
            {
                await _logService.LogAsync("ERROR", "Error creating book.", ex);
                return (false, "Error creating book.", null);
            }
        }

        public async Task<(bool Success, string Message)> UpdateAsync(Book updatedBook)
        {
            try
            {
                var result = await _books.ReplaceOneAsync(b => b.Id == updatedBook.Id, updatedBook);
                if (result.MatchedCount == 0)
                    return (false, "Book not found.");

                await _logService.LogAsync("INFORMATION", $"Book updated: {updatedBook.Id}");
                return (true, "Book updated successfully.");
            }
            catch (Exception ex)
            {
                await _logService.LogAsync("ERROR", "Error updating book.", ex);
                return (false, "Error updating book.");
            }
        }

        public async Task<(bool Success, string Message)> DeleteAsync(string id)
        {
            try
            {
                var result = await _books.DeleteOneAsync(b => b.Id == id);
                if (result.DeletedCount == 0)
                    return (false, "Book not found.");

                await _logService.LogAsync("WARNING", $"Book deleted: {id}");
                return (true, "Book deleted successfully.");
            }
            catch (Exception ex)
            {
                await _logService.LogAsync("ERROR", "Error deleting book.", ex);
                return (false, "Error deleting book.");
            }
        }
    }
}
