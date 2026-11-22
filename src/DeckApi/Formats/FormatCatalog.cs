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
}

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

    /// <summary>Format code → one definition per season, oldest first.</summary>
    private readonly Dictionary<string, List<FormatDefinition>> _seasons = new(StringComparer.Ordinal);
    private readonly List<FormatDefinition> _formats;
    private readonly TimeProvider _clock;

    public FormatCatalog(FormatConfigFile config, Func<string, Stream?> openFile, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var allowlists = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> Allowlist(string file, string code)
        {
            if (allowlists.TryGetValue(file, out var cached)) return cached;
            using var stream = openFile(file) ?? throw new FileNotFoundException($"Unique allowlist '{file}' not found for format '{code}'.");
            return allowlists[file] = LoadAllowlist(stream);
        }

        foreach (var f in config.Formats)
        {
            Prepare(f, config.Shared, Allowlist);
            if (f.Seasons.Count > 0)
            {
                _seasons[f.Code] = f.Seasons.Select(season =>
                {
                    var definition = ForSeason(f, season);
                    Prepare(definition, config.Shared, Allowlist);
                    definition.Season = season;
                    return definition;
                }).ToList();
            }
            _byCode[f.Code] = f;
            foreach (var alias in f.Aliases) _byCode[alias] = f;
        }
        _formats = config.Formats;
    }

    public IReadOnlyList<FormatDefinition> All => _formats.Select(Current).ToList();

    public FormatDefinition? Find(string? codeOrAlias) =>
        string.IsNullOrWhiteSpace(codeOrAlias) || !_byCode.TryGetValue(codeOrAlias.Trim(), out var f) ? null : Current(f);

    public IReadOnlyList<FormatDefinition> SeasonsOf(string? codeOrAlias)
    {
        if (string.IsNullOrWhiteSpace(codeOrAlias) || !_byCode.TryGetValue(codeOrAlias.Trim(), out var f)) return [];
        if (!_seasons.TryGetValue(f.Code, out var seasons)) return [];
        var now = _clock.GetUtcNow();
        return seasons.Where(s => s.Season!.HasStarted(now)).ToList();
    }

    public FormatDefinition? SeasonAt(string? codeOrAlias, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(codeOrAlias) || !_byCode.TryGetValue(codeOrAlias.Trim(), out var f)) return null;
        if (!_seasons.TryGetValue(f.Code, out var seasons)) return null;
        return seasons.LastOrDefault(s => s.Season!.HasStarted(at) && (s.Season.End is null || at < s.Season.End));
    }

    /// <summary>Last started season (the first one if none has started yet), or the base definition.</summary>
    private FormatDefinition Current(FormatDefinition f)
    {
        if (!_seasons.TryGetValue(f.Code, out var seasons)) return f;
        var now = _clock.GetUtcNow();
        return seasons.LastOrDefault(s => s.Season!.HasStarted(now)) ?? seasons[0];
    }

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
    private static HashSet<string> LoadAllowlist(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("included_refs", out var inc) ? inc
            : throw new InvalidDataException("Allowlist must be an array or have 'included_refs'.");
        return array.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    public static FormatCatalog Load(string? configDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            using var file = File.OpenRead(Path.Combine(configDirectory, "formats.json"));
            var config = JsonSerializer.Deserialize<FormatConfigFile>(file, JsonOptions)!;
            return new FormatCatalog(config, name =>
            {
                var path = Path.Combine(configDirectory, name);
                return File.Exists(path) ? File.OpenRead(path) : OpenEmbedded(name);
            });
        }

        using var embedded = OpenEmbedded("formats.json") ?? throw new InvalidOperationException("Embedded formats.json missing.");
        return new FormatCatalog(JsonSerializer.Deserialize<FormatConfigFile>(embedded, JsonOptions)!, OpenEmbedded);
    }

    private static Stream? OpenEmbedded(string fileName)
    {
        var assembly = typeof(FormatCatalog).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".Formats.Config." + fileName, StringComparison.Ordinal));
        return name is null ? null : assembly.GetManifestResourceStream(name);
    }
}
