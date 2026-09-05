using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services
{
    public class MongoDBService
    {
        public IMongoDatabase _database { get; }

        public MongoDBService(IConfiguration configuration)
        {
            try
            {
                var user = Environment.GetEnvironmentVariable("MONGO_USER");
                var password = Environment.GetEnvironmentVariable("MONGO_PASSWORD");
                var cluster = configuration["MongoDB:ClusterUri"];
                var dbName = configuration["MongoDB:DatabaseName"];

                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password) ||
                    string.IsNullOrWhiteSpace(cluster) || string.IsNullOrWhiteSpace(dbName))
                {
                    throw new InvalidOperationException("Faltan variables de entorno o configuraciones para la conexión a MongoDB.");
                }

                var encodedUser = Uri.EscapeDataString(user);
                var encodedPassword = Uri.EscapeDataString(password);
                var connectionString = $"mongodb+srv://{encodedUser}:{encodedPassword}@{cluster}/?retryWrites=true&w=majority";
                var client = new MongoClient(connectionString);
                _database = client.GetDatabase(dbName);

                Console.WriteLine($"✅ Conexión a MongoDB Atlas exitosa: {dbName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error al conectar con MongoDB Atlas: {ex.Message}");
                throw new InvalidOperationException("No se pudo establecer conexión con la base de datos MongoDB.", ex);
            }
        }

        public IMongoCollection<User> Users => _database.GetCollection<User>("Users");
        public IMongoCollection<LogEntry> LogEntries => _database.GetCollection<LogEntry>("LogEntries");
        public IMongoCollection<Book> Books => _database.GetCollection<Book>("Books");
        public IMongoCollection<Loan> Loans => _database.GetCollection<Loan>("Loans");
        public IMongoCollection<Favorite> Favorites => _database.GetCollection<Favorite>("Favorites");

        public async Task CreateIndexesAsync()
        {
            var userBuilder = Builders<User>.IndexKeys;
            var userIndexes = new[]
            {
                new CreateIndexModel<User>(userBuilder.Ascending(u => u.Username), new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<User>(userBuilder.Ascending(u => u.Email), new CreateIndexOptions { Unique = true })
            };
            await Users.Indexes.CreateManyAsync(userIndexes);

            var bookBuilder = Builders<Book>.IndexKeys;
            var isbnFilter = Builders<Book>.Filter.And(
                Builders<Book>.Filter.Exists(book => book.Isbn),
                Builders<Book>.Filter.Ne(book => book.Isbn, null),
                Builders<Book>.Filter.Ne(book => book.Isbn, string.Empty));
            var bookIndexes = new[]
            {
                new CreateIndexModel<Book>(
                    bookBuilder.Ascending(book => book.Isbn),
                    new CreateIndexOptions<Book> { Name = "ux_books_isbn", Unique = true, PartialFilterExpression = isbnFilter }),
                new CreateIndexModel<Book>(
                    bookBuilder.Text(book => book.Title).Text(book => book.Authors),
                    new CreateIndexOptions { Name = "tx_books_title_authors" }),
                new CreateIndexModel<Book>(
                    bookBuilder.Ascending(book => book.IsActive).Ascending(book => book.MediaType),
                    new CreateIndexOptions { Name = "ix_books_active_media" }),
                new CreateIndexModel<Book>(
                    bookBuilder.Ascending(book => book.Genres),
                    new CreateIndexOptions { Name = "ix_books_genres" }),
                new CreateIndexModel<Book>(
                    bookBuilder.Descending(book => book.CreatedAt).Descending(book => book.Id),
                    new CreateIndexOptions { Name = "ix_books_created_id" })
            };
            await Books.Indexes.CreateManyAsync(bookIndexes);

            var loanBuilder = Builders<Loan>.IndexKeys;
            var activeReservationFilter = Builders<Loan>.Filter.And(
                Builders<Loan>.Filter.Exists(loan => loan.ActiveReservationKey),
                Builders<Loan>.Filter.Ne(loan => loan.ActiveReservationKey, null));
            var loanIndexes = new[]
            {
                new CreateIndexModel<Loan>(
                    loanBuilder.Ascending(loan => loan.ActiveReservationKey),
                    new CreateIndexOptions<Loan> { Name = "ux_loans_active_reservation", Unique = true, PartialFilterExpression = activeReservationFilter }),
                new CreateIndexModel<Loan>(loanBuilder.Ascending(loan => loan.UserId).Descending(loan => loan.ReservedAt), new CreateIndexOptions { Name = "ix_loans_user_reserved" }),
                new CreateIndexModel<Loan>(loanBuilder.Ascending(loan => loan.BookId).Ascending(loan => loan.Status), new CreateIndexOptions { Name = "ix_loans_book_status" })
            };
            await Loans.Indexes.CreateManyAsync(loanIndexes);

            var favoriteBuilder = Builders<Favorite>.IndexKeys;
            await Favorites.Indexes.CreateManyAsync([
                new CreateIndexModel<Favorite>(favoriteBuilder.Ascending(item => item.UserId).Ascending(item => item.BookId), new CreateIndexOptions { Name = "ux_favorites_user_book", Unique = true }),
                new CreateIndexModel<Favorite>(favoriteBuilder.Ascending(item => item.UserId).Descending(item => item.CreatedAt), new CreateIndexOptions { Name = "ix_favorites_user_created" })
            ]);

            var logBuilder = Builders<LogEntry>.IndexKeys;
            var logIndexes = new[]
            {
                new CreateIndexModel<LogEntry>(logBuilder.Ascending(l => l.Timestamp), new CreateIndexOptions { Name = "ix_logs_timestamp" }),
                new CreateIndexModel<LogEntry>(logBuilder.Ascending(l => l.Level).Descending(l => l.Timestamp), new CreateIndexOptions { Name = "ix_logs_level_timestamp" }),
                new CreateIndexModel<LogEntry>(logBuilder.Ascending(l => l.EventType).Descending(l => l.Timestamp), new CreateIndexOptions { Name = "ix_logs_event_timestamp" })
            };
            await LogEntries.Indexes.CreateManyAsync(logIndexes);

            Console.WriteLine("✅ Índices creados exitosamente en MongoDB.");
        }
    }
}
