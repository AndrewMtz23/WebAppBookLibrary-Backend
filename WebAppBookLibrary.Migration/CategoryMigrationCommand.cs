using System.Text.Json;
using MongoDB.Driver;
using WebAppBookLibrary.Migrations;

internal static class CategoryMigrationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.FindIndex(args, arg => arg == name);
            return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[index + 1] : null;
        }
        var apply = args.Contains("--apply");
        var databaseName = Option("--database");
        var connectionEnvironment = Option("--connection-env");
        var mappingPath = Option("--mapping");
        if (string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(connectionEnvironment))
        {
            Console.Error.WriteLine("Categories require --database NAME --connection-env VARIABLE. Dry-run is the default; this mode never loads .env.");
            return 2;
        }
        if (apply && (!args.Contains("--snapshot-confirmed") || !args.Contains("--writes-paused") || mappingPath is null))
        {
            Console.Error.WriteLine("Apply requires --snapshot-confirmed --writes-paused --mapping FILE with reviewed equivalences.");
            return 2;
        }
        var connection = Environment.GetEnvironmentVariable(connectionEnvironment);
        if (string.IsNullOrWhiteSpace(connection)) { Console.Error.WriteLine("The explicit connection environment variable is missing."); return 3; }
        try
        {
            var mapping = mappingPath is null ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(mappingPath))
                    ?? throw new JsonException("Expected an object of original-to-canonical genre names.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var settings = MongoClientSettings.FromConnectionString(connection);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
            var report = await CategorySchemaMigration.RunAsync(new MongoClient(settings).GetDatabase(databaseName), mapping, apply, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return report.Anomalies.Count == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is MongoException or IOException or JsonException or ArgumentException or OperationCanceledException or InvalidOperationException)
        {
            Console.Error.WriteLine("Category migration stopped. Verify mapping, connectivity, indexes and concurrent writes; rerun dry-run before retrying. Connection details are not printed.");
            return 4;
        }
    }
}
