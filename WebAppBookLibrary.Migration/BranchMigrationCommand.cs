using System.Text.Json;
using MongoDB.Driver;
using WebAppBookLibrary.Migrations;
using WebAppBookLibrary.Services;

internal static class BranchMigrationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var database = Option("--database"); var variable = Option("--connection-env");
        var organization = Option("--organization-name"); var code = Option("--branch-code"); var name = Option("--branch-name"); var zone = Option("--time-zone");
        var apply = args.Contains("--apply"); var fingerprint = Option("--expected-fingerprint"); var actor = Option("--operator-id");
        if (new[] { database, variable, organization, code, name, zone }.Any(string.IsNullOrWhiteSpace) ||
            apply && (!args.Contains("--snapshot-confirmed") || !args.Contains("--offline-confirmed") || fingerprint is null || actor is null))
        {
            Console.Error.WriteLine("Use --branches --database NAME --connection-env VARIABLE --organization-name NAME --branch-code CODE --branch-name NAME --time-zone IANA. Dry-run is the default. Apply also requires --snapshot-confirmed --offline-confirmed --expected-fingerprint HASH --operator-id ADMIN_ID.");
            return 2;
        }
        var connection = Environment.GetEnvironmentVariable(variable!);
        if (string.IsNullOrWhiteSpace(connection)) return 3;
        try
        {
            var url = new MongoUrl(connection);
            if (!args.Contains("--allow-remote") && url.Servers.Any(s => s.Host is not ("localhost" or "127.0.0.1" or "::1")))
            { Console.Error.WriteLine("Remote target blocked. Remote operation requires explicit authorization and --allow-remote."); return 2; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var migration = new BranchMigration(new MongoDBService(new MongoClient(connection).GetDatabase(database)), TimeProvider.System);
            var input = new BranchMigrationInput(organization!, code!, name!, zone!);
            if (!apply) { var preview = await migration.PreviewAsync(input, timeout.Token); Console.WriteLine(JsonSerializer.Serialize(preview)); return preview.Eligible ? 0 : 1; }
            var result = await migration.ApplyAsync(input, fingerprint!, actor!, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(result)); return result.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is MongoException or InvalidOperationException or ArgumentException or OperationCanceledException)
        { Console.Error.WriteLine("Branch migration stopped. Rerun dry-run and inspect its report. No connection details are printed."); return 4; }
    }
}
