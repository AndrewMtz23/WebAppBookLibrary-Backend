using MongoDB.Driver;
using WebAppBookLibrary.Services;

internal static class CirculationMigrationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var database = Option("--database"); var variable = Option("--connection-env"); var mode = Option("--mode") ?? "active"; var apply = args.Contains("--apply");
        if (database == null || variable == null || (apply && (!args.Contains("--snapshot-confirmed") || !args.Contains("--writes-paused"))))
        { Console.Error.WriteLine("Use --database NAME --connection-env VARIABLE [--mode active|draining|legacy]. Apply requires --snapshot-confirmed --writes-paused. Dry-run is the default."); return 2; }
        var connection = Environment.GetEnvironmentVariable(variable); if (string.IsNullOrWhiteSpace(connection)) return 3;
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var mongo = new MongoDBService(new MongoClient(connection).GetDatabase(database));
            var report = await CirculationMaintenance.RunAsync(mongo, mode, apply, timeout.Token);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
            return report.Books.All(b => b.Consistent) ? 0 : 1;
        } catch (Exception e) when (e is MongoException or InvalidOperationException or ArgumentException or OperationCanceledException) {
            Console.Error.WriteLine("Circulation change stopped. Check inventory, references and mode; no connection details are printed. Rerun dry-run."); return 4;
        }
    }
}
