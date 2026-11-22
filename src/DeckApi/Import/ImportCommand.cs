using DeckApi.Data;
using DeckApi.Formats;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Import;

/// <summary><c>import-legacy --source "&lt;old db connection string&gt;" [--dry-run] [--migrate]</c></summary>
public static class ImportCommand
{
    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        string? source = null;
        bool dryRun = false, migrate = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" when i + 1 < args.Length: source = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                case "--migrate": migrate = true; break;
            }
        }
        if (source is null)
        {
            Console.Error.WriteLine("Usage: import-legacy --source \"Host=...;Database=...;Username=...;Password=...\" [--dry-run] [--migrate]");
            return 2;
        }

        using var scope = app.Services.CreateScope();
        if (migrate) await scope.ServiceProvider.GetRequiredService<DeckDbContext>().Database.MigrateAsync();
        // Validate with the season picked on the website, like the API does.
        await scope.ServiceProvider.GetRequiredService<FormatSeasonSelectionsLoader>().RefreshAsync(CancellationToken.None);
        var importer = scope.ServiceProvider.GetRequiredService<LegacyImporter>();
        var report = await importer.RunAsync(source, dryRun, CancellationToken.None);
        Console.WriteLine($"Users: {report.Users}, decks created: {report.DecksCreated}, updated: {report.DecksUpdated}, " +
                          $"unchanged: {report.DecksUnchanged}, upvotes: {report.Upvotes}, decks with uncompressible lines: {report.DecksUncompressible}");
        return 0;
    }
}
