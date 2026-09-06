using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Dashboard;
using WebAppBookLibrary.Domain.Loans;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class MongoDashboardStore(MongoDBService database) : IDashboardStore
{
    private readonly IMongoCollection<User> _users = database.Users;
    private readonly IMongoCollection<Book> _books = database.Books;
    private readonly IMongoCollection<Loan> _loans = database.Loans;
    private readonly IMongoCollection<Favorite> _favorites = database.Favorites;

    public async Task<User?> FindActiveUserAsync(string username, CancellationToken token) => await _users.Find(user => user.Username == username && user.IsActive).FirstOrDefaultAsync(token);

    public async Task<ReaderDashboardResponse> ReaderAsync(string userId, DashboardPeriod period, CancellationToken token)
    {
        var filter = Builders<Loan>.Filter.Eq(loan => loan.UserId, userId) & PeriodFilter(period);
        var total = await _loans.CountDocumentsAsync(filter, cancellationToken: token);
        var favorites = await _favorites.CountDocumentsAsync(item => item.UserId == userId, cancellationToken: token);
        var media = await GroupLoansByMedia(filter, token);
        return new(period.GeneratedAt, period.FromUtc, period.ToUtc, total, favorites, media);
    }

    public async Task<LibrarianDashboardResponse> LibrarianAsync(DashboardPeriod period, CancellationToken token)
    {
        var filter = PeriodFilter(period);
        var total = await _loans.CountDocumentsAsync(filter, cancellationToken: token);
        var active = await _loans.CountDocumentsAsync(filter & Builders<Loan>.Filter.In(loan => loan.Status, [LoanStatuses.Active, LoanStatuses.Overdue]), cancellationToken: token);
        var overdueFilter = Builders<Loan>.Filter.Eq(loan => loan.Status, LoanStatuses.Overdue) |
                            (Builders<Loan>.Filter.Eq(loan => loan.Status, LoanStatuses.Active) & Builders<Loan>.Filter.Lt(loan => loan.DueAt, period.GeneratedAt));
        var overdue = await _loans.CountDocumentsAsync(filter & overdueFilter, cancellationToken: token);
        var activeBooks = await _books.CountDocumentsAsync(book => book.IsActive, cancellationToken: token);
        var physical = await _books.Find(book => book.IsActive && book.MediaType == Domain.Books.MediaTypes.Physical).Project(book => book.AvailableCopies).ToListAsync(token);
        return new(period.GeneratedAt, period.FromUtc, period.ToUtc, total, active, overdue, activeBooks, physical.Sum(value => value ?? 0), await GroupLoansByMedia(filter, token));
    }

    public async Task<AdminDashboardResponse> AdminAsync(DashboardPeriod period, CancellationToken token)
    {
        var filter = PeriodFilter(period);
        var users = await _users.Aggregate().Match(user => user.IsActive).Group(user => user.Role, group => new MetricCount(group.Key, group.LongCount())).SortBy(item => item.Key).ToListAsync(token);
        var media = await GroupLoansByMedia(filter, token);
        return new(period.GeneratedAt, period.FromUtc, period.ToUtc,
            await _users.CountDocumentsAsync(user => user.IsActive, cancellationToken: token),
            await _books.CountDocumentsAsync(_ => true, cancellationToken: token),
            await _loans.CountDocumentsAsync(filter, cancellationToken: token), users, media);
    }

    private static FilterDefinition<Loan> PeriodFilter(DashboardPeriod period) =>
        Builders<Loan>.Filter.Gte(loan => loan.ReservedAt, period.FromUtc) & Builders<Loan>.Filter.Lt(loan => loan.ReservedAt, period.ToUtc);

    private async Task<IReadOnlyList<MetricCount>> GroupLoansByMedia(FilterDefinition<Loan> filter, CancellationToken token) =>
        await _loans.Aggregate().Match(filter).Group(loan => loan.MediaType, group => new MetricCount(group.Key, group.LongCount())).SortBy(item => item.Key).ToListAsync(token);
}
