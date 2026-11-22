using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeckApi.Formats;

public interface IFormatCatalog
{
    /// <summary>Every format, with the rules of its current season.</summary>
    IReadOnlyList<FormatDefinition> All { get; }

    /// <summary>Resolves a code or alias (case-insensitive) to the rules of its current season. Empty/null = no format.</summary>
    FormatDefinition? Find(string? codeOrAlias);

    /// <summary>One definition per season of the format, oldest first, started seasons only. Empty when the format has no seasons.</summary>
    IReadOnlyList<FormatDefinition> SeasonsOf(string? codeOrAlias);

    /// <summary>The season running at <paramref name="at"/> (start ≤ at &lt; end), null when none or no seasons.</summary>
    FormatDefinition? SeasonAt(string? codeOrAlias, DateTimeOffset at);

    /// <summary>Every season of the format (started or not), oldest first, with the dates actually in effect.</summary>
    IReadOnlyList<FormatSeason> SeasonListOf(string? codeOrAlias);
}

/// <summary>
/// A season picked on the website's admin page (see <see cref="FormatSeasonSelector"/>). It brings its own unique
/// allowlist, and the most recently selected season of a format is its current one.
/// </summary>
public sealed record SeasonSelection(
    string Format, string Season, string Label, IReadOnlySet<string> UniqueAllowlist,
    DateTimeOffset FirstSelectedAt, DateTimeOffset SelectedAt);

public sealed class FormatConfigFile
{
    /// <summary>Shared lists applied to every format that does not allow banned/suspended cards.</summary>
    public SharedLists Shared { get; init; } = new();
    public List<FormatDefinition> Formats { get; init; } = [];

    public sealed class SharedLists
    {
        public string[] Banned { get; init; } = [];
        public string[] Suspended { get; init; } = [];
    }
}

/// <summary>
/// Loads format rules from the embedded <c>Formats/Config/formats.json</c>, or from
/// <c>Formats:ConfigPath</c> when set (a directory holding <c>formats.json</c> and the allowlist files),
/// so ban lists can change without a rebuild. Call <c>POST /api/admin/decks/revalidate</c> afterwards.
/// Season selections made on the website (<see cref="ApplySeasonSelections"/>) are layered on top.
/// </summary>
public sealed class FormatCatalog : IFormatCatalog
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, FormatDefinition> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FormatDefinition> _formats;
    private readonly FormatConfigFile.SharedLists _shared;
    private readonly Func<string, string, HashSet<string>> _allowlist;
    private readonly TimeProvider _clock;

    /// <summary>Swapped as a whole by <see cref="ApplySeasonSelections"/>, so readers never see a half-built state.</summary>
    private volatile SeasonState _state;

    /// <param name="Seasons">Format code → one definition per season, oldest first.</param>
    /// <param name="Pinned">Formats whose current season was picked on the website: it is their last season.</param>
    private sealed record SeasonState(Dictionary<string, List<FormatDefinition>> Seasons, HashSet<string> Pinned);

    public FormatCatalog(FormatConfigFile config, Func<string, Stream?> openFile, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _shared = config.Shared;
        var allowlists = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        _allowlist = (file, code) =>
        {
            lock (allowlists)
            {
                if (allowlists.TryGetValue(file, out var cached)) return cached;
                using var stream = openFile(file) ?? throw new FileNotFoundException($"Unique allowlist '{file}' not found for format '{code}'.");
                return allowlists[file] = LoadAllowlist(stream);
            }
        };

        foreach (var f in config.Formats)
        {
            Prepare(f, _shared, _allowlist);
            _byCode[f.Code] = f;
            foreach (var alias in f.Aliases) _byCode[alias] = f;
        }
        _formats = config.Formats;
        _state = BuildSeasons([]);
    }

    public IReadOnlyList<FormatDefinition> All => _formats.Select(Current).ToList();

    public FormatDefinition? Find(string? codeOrAlias) => Resolve(codeOrAlias) is { } f ? Current(f) : null;

    public IReadOnlyList<FormatDefinition> SeasonsOf(string? codeOrAlias)
    {
        if (Resolve(codeOrAlias) is not { } f || !_state.Seasons.TryGetValue(f.Code, out var seasons)) return [];
        var now = _clock.GetUtcNow();
        return seasons.Where(s => s.Season!.HasStarted(now)).ToList();
    }

    public FormatDefinition? SeasonAt(string? codeOrAlias, DateTimeOffset at)
    {
        if (Resolve(codeOrAlias) is not { } f || !_state.Seasons.TryGetValue(f.Code, out var seasons)) return null;
        return seasons.LastOrDefault(s => s.Season!.HasStarted(at) && (s.Season.End is null || at < s.Season.End));
    }

    public IReadOnlyList<FormatSeason> SeasonListOf(string? codeOrAlias) =>
        Resolve(codeOrAlias) is { } f && _state.Seasons.TryGetValue(f.Code, out var seasons) ? seasons.Select(s => s.Season!).ToList() : [];

    /// <summary>
    /// Replaces the season selections made on the website (all of them, every format). For each format, the most
    /// recently selected season becomes the current one:
    /// <list type="bullet">
    /// <item>a season declared in <c>formats.json</c> keeps its bans but takes the selection's allowlist;</item>
    /// <item>an undeclared season (e.g. S5 before any deploy) is added, with the format's rules and that allowlist;</item>
    /// <item>it starts when first selected, unless <c>formats.json</c> declares an earlier start, and has no end;</item>
    /// <item>the previous season ends when it starts; later seasons are dropped (going back to S4 after S5 makes S4 current again).</item>
    /// </list>
    /// </summary>
    public void ApplySeasonSelections(IReadOnlyCollection<SeasonSelection> selections) => _state = BuildSeasons(selections);

    private FormatDefinition? Resolve(string? codeOrAlias) =>
        string.IsNullOrWhiteSpace(codeOrAlias) || !_byCode.TryGetValue(codeOrAlias.Trim(), out var f) ? null : f;

    /// <summary>The selected season, else the last started one (the first one if none has started yet), or the base definition.</summary>
    private FormatDefinition Current(FormatDefinition f)
    {
        var state = _state;
        if (!state.Seasons.TryGetValue(f.Code, out var seasons)) return f;
        if (state.Pinned.Contains(f.Code)) return seasons[^1];
        var now = _clock.GetUtcNow();
        return seasons.LastOrDefault(s => s.Season!.HasStarted(now)) ?? seasons[0];
    }

    private SeasonState BuildSeasons(IReadOnlyCollection<SeasonSelection> selections)
    {
        var result = new Dictionary<string, List<FormatDefinition>>(StringComparer.Ordinal);
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in _formats)
        {
            var mine = selections.Where(s => string.Equals(s.Format, f.Code, StringComparison.OrdinalIgnoreCase)).ToList();
            if (f.Seasons.Count == 0 && mine.Count == 0) continue;

            var entries = new List<SeasonEntry>();
            foreach (var declared in f.Seasons)
            {
                var selection = mine.FirstOrDefault(s => string.Equals(s.Season, declared.Code, StringComparison.OrdinalIgnoreCase));
                // A selected season never starts later than the moment it was picked.
                var start = selection is not null && declared.Start > selection.FirstSelectedAt ? selection.FirstSelectedAt : declared.Start;
                entries.Add(new(declared, selection, start));
            }
            foreach (var selection in mine.Where(s => !f.Seasons.Any(d => string.Equals(d.Code, s.Season, StringComparison.OrdinalIgnoreCase))))
                entries.Add(new(new FormatSeason { Code = selection.Season, Label = selection.Label }, selection, selection.FirstSelectedAt));

            var ordered = entries.OrderBy(e => e.Start ?? DateTimeOffset.MinValue).ToList(); // stable: declared order on ties
            if (mine.MaxBy(s => s.SelectedAt) is { } current)
            {
                var index = ordered.FindIndex(e => e.Selection == current);
                ordered.RemoveRange(index + 1, ordered.Count - index - 1);
                pinned.Add(f.Code);
            }

            result[f.Code] = ordered.Select((e, i) =>
            {
                var end = e.Declared.End;
                if (i + 1 < ordered.Count && ordered[i + 1].Start is { } nextStart && (end is null || end > nextStart)) end = nextStart;
                if (i == ordered.Count - 1 && pinned.Contains(f.Code)) end = null; // current until another season is selected
                var season = new FormatSeason
                {
                    Code = e.Declared.Code, Label = e.Declared.Label, Start = e.Start, End = end,
                    UniqueAllowlist = e.Selection is null ? e.Declared.UniqueAllowlist : null,
                    Banned = e.Declared.Banned, Suspended = e.Declared.Suspended,
                };
                var definition = ForSeason(f, season);
                Prepare(definition, _shared, _allowlist);
                if (e.Selection is { } selection) definition.UniqueAllowlistRefs = selection.UniqueAllowlist.ToHashSet(StringComparer.Ordinal);
                definition.Season = season;
                return definition;
            }).ToList();
        }
        return new SeasonState(result, pinned);
    }

    private sealed record SeasonEntry(FormatSeason Declared, SeasonSelection? Selection, DateTimeOffset? Start);

    private static void Prepare(FormatDefinition f, FormatConfigFile.SharedLists shared, Func<string, string, HashSet<string>> allowlist)
    {
        f.EffectiveBanned = shared.Banned.Concat(f.Banned).Distinct().ToArray();
        f.EffectiveSuspended = shared.Suspended.Concat(f.Suspended).Distinct().ToArray();
        f.BannedMatcher = new CardMatcher(f.EffectiveBanned);
        f.SuspendedMatcher = new CardMatcher(f.EffectiveSuspended);
        if (f.UniqueAllowlist is { } file) f.UniqueAllowlistRefs = allowlist(file, f.Code);
    }

    /// <summary>Copy of the format with the season's overrides: allowlist replaced, bans and suspensions added.</summary>
    private static FormatDefinition ForSeason(FormatDefinition f, FormatSeason season)
    {
        var json = JsonSerializer.SerializeToNode(f, JsonOptions)!.AsObject();
        if (season.UniqueAllowlist is { } file) json["uniqueAllowlist"] = file;
        json["banned"] = JsonSerializer.SerializeToNode(f.Banned.Concat(season.Banned).Distinct().ToArray(), JsonOptions);
        json["suspended"] = JsonSerializer.SerializeToNode(f.Suspended.Concat(season.Suspended).Distinct().ToArray(), JsonOptions);
        return json.Deserialize<FormatDefinition>(JsonOptions)!;
    }

    /// <summary>Accepts uniques-search-api's format file shape (<c>included_refs</c>) or a plain string array.</summary>
    internal static HashSet<string> LoadAllowlist(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("included_refs", out var inc) ? inc
            : throw new InvalidDataException("Allowlist must be an array or have 'included_refs'.");
        return array.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    public static FormatCatalog Load(string? configDirectory, TimeProvider? clock = null)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            using var file = File.OpenRead(Path.Combine(configDirectory, "formats.json"));
            var config = JsonSerializer.Deserialize<FormatConfigFile>(file, JsonOptions)!;
            return new FormatCatalog(config, name =>
            {
                var path = Path.Combine(configDirectory, name);
                return File.Exists(path) ? File.OpenRead(path) : OpenEmbedded(name);
            }, clock);
        }

        using var embedded = OpenEmbedded("formats.json") ?? throw new InvalidOperationException("Embedded formats.json missing.");
        return new FormatCatalog(JsonSerializer.Deserialize<FormatConfigFile>(embedded, JsonOptions)!, OpenEmbedded, clock);
    }

    private static Stream? OpenEmbedded(string fileName)
    {
        var assembly = typeof(FormatCatalog).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".Formats.Config." + fileName, StringComparison.Ordinal));
        return name is null ? null : assembly.GetManifestResourceStream(name);
    }
}
