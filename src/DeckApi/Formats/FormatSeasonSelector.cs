using System.Text.RegularExpressions;
using DeckApi.Admin;
using DeckApi.Data;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Formats;

public enum SeasonSelectionError { InvalidId, UnknownFormat, NotInManifest, EmptyAllowlist, ManifestUnavailable }

public sealed class SeasonSelectionException(SeasonSelectionError error, string message) : Exception(message)
{
    public SeasonSelectionError Error { get; } = error;
}

/// <summary>
/// Applies the Frontier season picked on the website's admin page (<c>core-altered-cards</c> › Uniques format), which
/// stores a formats manifest id such as <c>frontier_s05</c>. The season's allowlist is read from the manifest, the
/// season becomes the format's current one (persisted in <c>format_season_selections</c>, so every instance and every
/// restart agree) and the format's decks are revalidated in the background.
/// </summary>
public sealed partial class FormatSeasonSelector(
    DeckDbContext db, FormatCatalog catalog, FormatsManifestClient manifest, RevalidationQueue revalidations,
    TimeProvider clock, ILogger<FormatSeasonSelector> logger)
{
    /// <summary><c>frontier_s05</c>, <c>frontier.s5</c>, <c>frontier-S05</c> → (<c>frontier</c>, 5).</summary>
    [GeneratedRegex(@"^(?<format>[A-Za-z][A-Za-z0-9_-]*?)[_.-][sS](?<number>\d{1,3})$")]
    private static partial Regex ManifestIdPattern();

    public async Task<FormatSeasonSelection> SelectAsync(string manifestId, CancellationToken ct)
    {
        manifestId = manifestId.Trim();
        var match = ManifestIdPattern().Match(manifestId);
        if (!match.Success)
            throw new SeasonSelectionException(SeasonSelectionError.InvalidId,
                $"\"{manifestId}\" is not a season id. Expected <format>_sNN, e.g. frontier_s05.");
        var format = catalog.Find(match.Groups["format"].Value)
            ?? throw new SeasonSelectionException(SeasonSelectionError.UnknownFormat, $"Unknown format \"{match.Groups["format"].Value}\".");
        var number = int.Parse(match.Groups["number"].Value);
        var seasonCode = $"S{number}";

        FormatsManifestEntry entry;
        HashSet<string> allowlist;
        try
        {
            entry = (await manifest.GetManifestAsync(ct)).FirstOrDefault(e => string.Equals(e.Id, manifestId, StringComparison.OrdinalIgnoreCase))
                ?? throw new SeasonSelectionException(SeasonSelectionError.NotInManifest, $"\"{manifestId}\" is not in the formats manifest.");
            allowlist = await manifest.GetAllowlistAsync(entry, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException && !ct.IsCancellationRequested)
        {
            throw new SeasonSelectionException(SeasonSelectionError.ManifestUnavailable, $"The formats manifest could not be read: {e.Message}");
        }
        if (allowlist.Count == 0)
            throw new SeasonSelectionException(SeasonSelectionError.EmptyAllowlist, $"\"{entry.Path}\" has no allowed Unique.");

        var now = clock.GetUtcNow();
        var row = await db.FormatSeasonSelections.FirstOrDefaultAsync(s => s.Format == format.Code && s.Season == seasonCode, ct);
        if (row is null)
        {
            row = new FormatSeasonSelection
            {
                Format = format.Code, Season = seasonCode, Label = $"Saison {number}", ManifestId = entry.Id, FirstSelectedAt = now,
            };
            db.FormatSeasonSelections.Add(row);
        }
        row.ManifestId = entry.Id;
        row.ManifestVersion = entry.Version;
        row.UniqueAllowlist = allowlist.Order(StringComparer.Ordinal).ToArray();
        row.SelectedAt = now;
        await db.SaveChangesAsync(ct);

        await LoadIntoAsync(db, catalog, ct);
        revalidations.Enqueue(format.Code);
        logger.LogInformation("{Format} season set to {Season} ({ManifestId} v{Version}, {Count} Uniques); revalidation queued",
            format.Code, seasonCode, entry.Id, entry.Version, allowlist.Count);
        return row;
    }

    /// <summary>Applies every stored selection to the catalog.</summary>
    public static async Task LoadIntoAsync(DeckDbContext db, FormatCatalog catalog, CancellationToken ct)
    {
        var rows = await db.FormatSeasonSelections.AsNoTracking().ToListAsync(ct);
        catalog.ApplySeasonSelections(rows.Select(r => new SeasonSelection(
            r.Format, r.Season, r.Label, r.UniqueAllowlist.ToHashSet(StringComparer.Ordinal), r.FirstSelectedAt, r.SelectedAt)).ToList());
    }
}

/// <summary>
/// Loads the website's season selections at startup (before the API serves requests), then checks every
/// <c>Formats:SelectionRefreshSeconds</c> (default 60, 0 = never) whether another instance changed them.
/// </summary>
public sealed class FormatSeasonSelectionsLoader(FormatCatalog catalog, IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<FormatSeasonSelectionsLoader> logger) : BackgroundService
{
    private (int Count, DateTimeOffset? LastSelectedAt)? _loaded;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = config.GetValue("Formats:SelectionRefreshSeconds", 60);
        if (seconds <= 0) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RefreshAsync(stoppingToken);
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeckDbContext>();
            var fingerprint = (await db.FormatSeasonSelections.CountAsync(ct), await db.FormatSeasonSelections.MaxAsync(s => (DateTimeOffset?)s.SelectedAt, ct));
            if (fingerprint == _loaded) return;
            await FormatSeasonSelector.LoadIntoAsync(db, catalog, ct);
            _loaded = fingerprint;
            foreach (var format in catalog.All.Where(f => f.Season is not null))
                logger.LogInformation("{Format} current season: {Season}", format.Code, format.Season!.Code);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not load the format season selections");
        }
    }
}
