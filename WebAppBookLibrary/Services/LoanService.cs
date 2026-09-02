using MongoDB.Driver;
using MongoDB.Bson;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services
{
    public class LoanService
    {
        private const string ERROR = "ERROR";
        private const string WARNING = "WARNING";
        private const string INFORMATION = "INFORMATION";

        private readonly IMongoCollection<Loan> _loans;
        private readonly IMongoCollection<Book> _books;
        private readonly IMongoCollection<User> _users;
        private readonly Logservice _logService;

        public LoanService(MongoDBService dbService, Logservice logService)
        {
            _loans = dbService.Loans;
            _books = dbService.Books;
            _users = dbService.Users;
            _logService = logService;
        }

        public async Task<(bool Success, string Message, Loan? Loan)> CreateLoanAsync(string bookId, string username)
        {
            try
            {
                var book = await _books.Find(b => b.Id == bookId).FirstOrDefaultAsync();
                if (book == null)
                    return (false, "Book not found", null);
                if (!book.IsAvailable)
                    return (false, "Book is not available for loan", null);

                var user = await _users.Find(u => u.Username == username).FirstOrDefaultAsync();
                if (user == null || !user.IsActive)
                    return (false, "Invalid or inactive user", null);

                var loan = new Loan
                {
                    BookId = bookId,
                    UserId = user.Id,
                    LoanDate = DateTime.UtcNow,
                    IsReturned = false
                };

                await _loans.InsertOneAsync(loan);

                var update = Builders<Book>.Update.Set(b => b.IsAvailable, false);
                var result = await _books.UpdateOneAsync(b => b.Id == bookId, update);

                if (result.MatchedCount == 0)
                {
                    await _logService.LogAsync(ERROR, $"Book {bookId} not matched for update.");
                    return (false, "Book update failed: not matched", null);
                }

                if (result.ModifiedCount == 0)
                {
                    await _logService.LogAsync(WARNING, $"Book {bookId} matched but not modified.");
                    return (false, "Book update failed: not modified", null);
                }

                await _logService.LogAsync(INFORMATION, $"Loan created by {username} for book {bookId}");
                return (true, "Loan created successfully", loan);
            }
            catch (Exception ex)
            {
                await _logService.LogAsync(ERROR, $"Error creating loan for book {bookId} by {username}.", ex);
                return (false, "Unexpected error occurred while creating loan", null);
            }
        }

        public async Task<List<object>> GetLoansByUsernameAsync(string username)
        {
            try
            {
                var user = await _users.Find(u => u.Username == username).FirstOrDefaultAsync();
                if (user == null) return new List<object>();

                var loans = await _loans.Find(l => l.UserId == user.Id).ToListAsync();

                var result = new List<object>();

                foreach (var loan in loans)
                {
                    var book = await _books.Find(b => b.Id == loan.BookId).FirstOrDefaultAsync();
                    var dueDate = loan.LoanDate.AddDays(14); // plazo de 14 días

                    string status = "active";
                    if (loan.IsReturned)
                        status = "returned";
                    else if (DateTime.UtcNow > dueDate)
                        status = "overdue";

                    result.Add(new
                    {
                        id = loan.Id,
                        bookTitle = book?.Title ?? "N/A",
                        loanDate = loan.LoanDate,
                        dueDate = dueDate,
                        returnDate = loan.ReturnDate,
                        status = status
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                await _logService.LogAsync("ERROR", $"Error fetching loans for user {username}.", ex);
                return new List<object>();
            }
        }


        public async Task<List<Loan>> GetAllLoansAsync()
        {
            try
            {
                return await _loans.Find(_ => true).ToListAsync();
            }
            catch (Exception ex)
            {
                await _logService.LogAsync(ERROR, "Error fetching all loans.", ex);
                return new List<Loan>();
            }
        }

        public async Task<List<object>> GetAllLoansWithDetailsAsync()
        {
            try
            {
                var loans = await _loans.Find(_ => true).ToListAsync();
                var result = new List<object>();

                foreach (var loan in loans)
                {
                    var book = await _books.Find(b => b.Id == loan.BookId).FirstOrDefaultAsync();
                    var user = await _users.Find(u => u.Id == loan.UserId).FirstOrDefaultAsync();
                    var dueDate = loan.LoanDate.AddDays(14); // plazo de 14 días

                    string status = "active";
                    if (loan.IsReturned)
                        status = "returned";
                    else if (DateTime.UtcNow > dueDate)
                        status = "overdue";

                    result.Add(new
                    {
                        id = loan.Id,
                        bookId = loan.BookId,
                        bookTitle = book?.Title ?? "N/A",
                        bookAuthor = book?.Author ?? "N/A",
                        userId = loan.UserId,
                        username = user?.Username ?? "N/A",
                        userEmail = user?.Email ?? "N/A",
                        loanDate = loan.LoanDate,
                        dueDate = dueDate,
                        returnDate = loan.ReturnDate,
                        status = status,
                        isOverdue = DateTime.UtcNow > dueDate && !loan.IsReturned
                    });
                }

                return result.OrderByDescending(x => ((dynamic)x).loanDate).ToList();
            }
            catch (Exception ex)
            {
                await _logService.LogAsync(ERROR, "Error fetching all loans with details.", ex);
                return new List<object>();
            }
        }

        public async Task<(bool Success, string Message)> MarkAsReturnedAsync(string loanId, string username)
        {
            try
            {
                var objectId = ObjectId.Parse(loanId);
                var loan = await _loans.Find(l => l.Id == objectId.ToString() && !l.IsReturned).FirstOrDefaultAsync();

                if (loan == null)
                {
                    await _logService.LogAsync(WARNING, $"Loan {loanId} not found.");
                    return (false, "Loan not found.");
                }

                var user = await _users.Find(u => u.Username == username).FirstOrDefaultAsync();
                if (user == null)
                {
                    await _logService.LogAsync(WARNING, $"User {username} not found trying to return loan {loanId}.");
                    return (false, "Unauthorized.");
                }

                var isAdminOrLibrarian = user.Role.ToLower() == "admin" || user.Role.ToLower() == "librarian";

                // Validar que el préstamo le pertenezca si es usuario normal
                if (!isAdminOrLibrarian && loan.UserId != user.Id)
                {
                    await _logService.LogAsync(WARNING, $"User {username} tried to return a loan not belonging to them.");
                    return (false, "You are not allowed to return this loan.");
                }

                loan.IsReturned = true;
                loan.ReturnDate = DateTime.UtcNow;

                await _loans.ReplaceOneAsync(l => l.Id == objectId.ToString(), loan);

                var update = Builders<Book>.Update.Set(b => b.IsAvailable, true);
                var updateResult = await _books.UpdateOneAsync(b => b.Id == loan.BookId, update);

                if (updateResult.MatchedCount == 0)
                {
                    await _logService.LogAsync(ERROR, $"Book {loan.BookId} not matched for update while returning loan {loanId}.");
                    return (false, "Book update failed: not matched.");
                }

                if (updateResult.ModifiedCount == 0)
                {
                    await _logService.LogAsync(WARNING, $"Book {loan.BookId} matched but not modified for loan {loanId}.");
                }

                await _logService.LogAsync(INFORMATION, $"Loan {loanId} marked as returned by {username}.");
                return (true, "Loan marked as returned.");
            }
            catch (Exception ex)
            {
                await _logService.LogAsync(ERROR, $"Error returning loan {loanId}.", ex);
                return (false, "Error returning loan.");
            }
        }


        public async Task<(bool Success, string Message)> DeleteLoanAsync(string loanId)
        {
            try
            {
                var objectId = ObjectId.Parse(loanId);
                var loan = await _loans.Find(l => l.Id == objectId.ToString()).FirstOrDefaultAsync();
                if (loan == null) return (false, "Loan not found.");

                if (!loan.IsReturned)
                {
                    var update = Builders<Book>.Update.Set(b => b.IsAvailable, true);
                    await _books.UpdateOneAsync(b => b.Id == loan.BookId, update);
                }

                await _loans.DeleteOneAsync(l => l.Id == objectId.ToString());
                await _logService.LogAsync(WARNING, $"Loan {loanId} deleted.");
                return (true, "Loan deleted successfully.");
            }
            catch (Exception ex)
            {
                await _logService.LogAsync(ERROR, $"Error deleting loan {loanId}.", ex);
                return (false, "Error deleting loan.");
            }
        }
    }
}
