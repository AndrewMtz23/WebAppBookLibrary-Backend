using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;
using AppProgram = WebAppBookLibrary.Program;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = "Development" });
builder.WebHost.UseUrls("http://127.0.0.1:7184");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Configuration["Jwt:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
builder.Configuration["Jwt:Issuer"] = "BookLibraryLocalQa";
builder.Configuration["Jwt:Audience"] = "BookLibraryLocalQa";
void Configure(string name, params object[] values) => typeof(AppProgram).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, values);
Configure("ConfigureCors", builder.Services, "http://localhost:4284");
Configure("ConfigureJwt", builder.Services, builder.Configuration);
Configure("ConfigureServices", builder.Services);
// Browser suites share loopback and intentionally authenticate repeatedly.
// Production's five-attempt limit remains unchanged; this suite does not test throttling.
builder.Services.RemoveAll<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>>();
builder.Services.AddRateLimiter(options => options.AddPolicy("auth", _ =>
    System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("local-browser-qa")));
Configure("ConfigureSwagger", builder.Services);
builder.Services.AddControllers().AddApplicationPart(typeof(WebAppBookLibrary.Controllers.BooksController).Assembly);
var client = new MongoClient("mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa");
var databaseName = "booklibrary_ui_test_" + Guid.NewGuid().ToString("N");
var database = client.GetDatabase(databaseName);
var mongo = new MongoDBService(database);
builder.Services.AddSingleton(mongo);
var password = "QaLocalOnly!2026";
var identities = new[] { "admin", "librarian", "user" }.Select(role => new User {
    Id = ObjectId.GenerateNewId().ToString(), Username = "qa_" + role, NormalizedUsername = ("qa_" + role).ToUpperInvariant(),
    DisplayName = "Prueba " + role, Email = role + "@booklibrary.invalid", NormalizedEmail = (role + "@booklibrary.invalid").ToUpperInvariant(),
    Role = role, PasswordHash = PasswordHasher.HashPassword(password), IsActive = true
}).ToArray();
await mongo.Users.InsertManyAsync(identities);
var books = new[] {
    new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Atlas de lectura", Authors = ["Ana Ejemplo"], Description = "Una colección de lecturas para comprobar la gestión del catálogo físico.", Genres = ["Ensayo"], Tags = ["Prueba"], MediaType = "physical", TotalCopies = 3, AvailableCopies = 3 },
    new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Cuaderno de historias", Authors = ["Luis Ejemplo"], Description = "Relatos digitales de ejemplo para comprobar la edición y el acceso al recurso.", Genres = ["Narrativa"], Tags = ["Prueba"], MediaType = "digital", DigitalResourceUrl = "https://www.gutenberg.org/ebooks/84" },
    new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Último ejemplar", Authors = ["Eva Ejemplo"], Description = "Título físico de prueba con inventario bajo para verificar los filtros operativos.", Genres = ["Narrativa"], MediaType = "physical", TotalCopies = 1, AvailableCopies = 1 },
    new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Archivo inactivo", Authors = ["Ana Ejemplo"], Description = "Título inactivo sin referencias que permite comprobar el borrado definitivo local.", Genres = ["Ensayo"], MediaType = "physical", TotalCopies = 1, AvailableCopies = 1, IsActive = false }
};
await mongo.Books.InsertManyAsync(books);
var auditNow = DateTime.UtcNow;
var auditEvents = Enumerable.Range(0, 10).Select(index => new LogEntry {
    Id = ObjectId.GenerateNewId().ToString(), Timestamp = auditNow.AddMinutes(-14).AddMinutes(index),
    Level = "WARNING", Message = "Authentication rejected.", EventType = "authentication.failed",
    ActorId = "anonymous", TargetType = "authentication", TargetId = "anonymous", IP = "192.0.2.47",
    StatusCode = 401, CorrelationId = "qa-auth-" + index,
    Metadata = new Dictionary<string, string> { ["reasonCode"] = "invalid_credentials", ["statusCode"] = "401" }
}).Concat(new[] {
    new LogEntry { Id = ObjectId.GenerateNewId().ToString(), Timestamp = auditNow.AddMinutes(-2), Level = "ERROR", Message = "Request failed safely.", EventType = "request.observed", Controller = "Books", Method = "GET", StatusCode = 503, IP = "2001:db8::1234", CorrelationId = "qa-http-503", Metadata = new Dictionary<string,string>{{"statusCode","503"}} },
    new LogEntry { Id = ObjectId.GenerateNewId().ToString(), Timestamp = auditNow.AddMinutes(-1), Level = "INFORMATION", Message = "Book updated.", EventType = "book.updated", ActorUsername = "qa_admin", TargetType = "book", TargetId = books[0].Id, IP = "127.0.0.1", CorrelationId = "qa-book-update", Metadata = new Dictionary<string,string>{{"result","success"}} }
}).ToArray();
await mongo.LogEntries.InsertManyAsync(auditEvents);
await mongo.CreateIndexesAsync();
await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "qa-session.json"), JsonSerializer.Serialize(new { databaseName, password, users = identities.Select(u => new { u.Username, u.Role }), books = books.Select(b => new { b.Id, b.Title }) }));
var app = builder.Build();
Configure("ConfigurePipeline", app);
// This route exists only in this isolated test host, never in the application.
app.MapGet("/__qa", () => new { fixture = "booklibrary-phase5", databaseName, books = books.Select(b => new { b.Id, b.Title }) });
Console.WriteLine("Local UI QA ready on 127.0.0.1:7184; session metadata is in qa-session.json beside the executable.");
try { await app.RunAsync(); }
finally { await client.DropDatabaseAsync(databaseName); }
