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

                var connectionString = $"mongodb+srv://{user}:{password}@{cluster}/?retryWrites=true&w=majority";
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

        public async Task CreateIndexesAsync()
        {
            var userBuilder = Builders<User>.IndexKeys;
            var userIndexes = new[]
            {
                new CreateIndexModel<User>(userBuilder.Ascending(u => u.Username), new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<User>(userBuilder.Ascending(u => u.Email), new CreateIndexOptions { Unique = true })
            };
            await Users.Indexes.CreateManyAsync(userIndexes);

            var logBuilder = Builders<LogEntry>.IndexKeys;
            var logIndexes = new[]
            {
                new CreateIndexModel<LogEntry>(logBuilder.Ascending(l => l.Timestamp))
            };
            await LogEntries.Indexes.CreateManyAsync(logIndexes);

            Console.WriteLine("✅ Índices creados exitosamente en MongoDB.");
        }
    }
}
