using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Migrations;

EnvironmentFileLoader.Load(Directory.GetCurrentDirectory());
var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
var snapshotConfirmed = args.Contains("--snapshot-confirmed", StringComparer.OrdinalIgnoreCase);
if (apply && !snapshotConfirmed)
{
    Console.Error.WriteLine("--apply requires --snapshot-confirmed.");
    return 2;
}

var user = Environment.GetEnvironmentVariable("MONGO_USER");
var password = Environment.GetEnvironmentVariable("MONGO_PASSWORD");
var cluster = Environment.GetEnvironmentVariable("MONGO_CLUSTER");
var databaseName = Environment.GetEnvironmentVariable("MONGO_DATABASE");
if (new[] { user, password, cluster, databaseName }.Any(string.IsNullOrWhiteSpace))
{
    Console.Error.WriteLine("MongoDB environment is incomplete.");
    return 3;
}

var uri = $"mongodb+srv://{Uri.EscapeDataString(user!)}:{Uri.EscapeDataString(password!)}@{cluster}/?retryWrites=true&w=majority";
var collection = new MongoClient(uri).GetDatabase(databaseName).GetCollection<BsonDocument>("Books");
var report = await BookSchemaMigration.RunAsync(collection, apply, CancellationToken.None);
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
return report.Anomalous > 0 ? 1 : 0;
