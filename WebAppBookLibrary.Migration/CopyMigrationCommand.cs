using System.Text.Json;
using MongoDB.Driver;
using WebAppBookLibrary.Migrations;
using WebAppBookLibrary.Services;

internal static class CopyMigrationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        var database=Option("--database");var variable=Option("--connection-env");var bookId=Option("--book-id");
        var apply=args.Contains("--apply");var fingerprint=Option("--expected-fingerprint");var actor=Option("--operator-id");
        if(database is null||variable is null||bookId is null||(apply&&(!args.Contains("--snapshot-confirmed")||fingerprint is null||actor is null)))
        {Console.Error.WriteLine("Use --copies --database NAME --connection-env VARIABLE --book-id ID. Apply requires --snapshot-confirmed --expected-fingerprint HASH --operator-id ADMIN_ID. Dry-run is the default.");return 2;}
        var connection=Environment.GetEnvironmentVariable(variable);if(string.IsNullOrWhiteSpace(connection))return 3;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var mongo=new MongoDBService(new MongoClient(connection).GetDatabase(database));
            var migration=new CopyInventoryMigration(mongo,TimeProvider.System);
            if(!apply){var preview=await migration.PreviewAsync(bookId,timeout.Token);Console.WriteLine(JsonSerializer.Serialize(preview));return preview.Eligible?0:1;}
            var result=await migration.ApplyAsync(bookId,fingerprint!,actor!,timeout.Token);Console.WriteLine(JsonSerializer.Serialize(result));return result.Success?0:1;
        }
        catch(Exception ex)when(ex is MongoException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {Console.Error.WriteLine("Copy conversion stopped. Check the local report and rerun dry-run; no connection details are printed.");return 4;}
    }
}
