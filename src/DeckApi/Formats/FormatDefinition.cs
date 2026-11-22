using System.Text.Json.Serialization;
using DeckApi.Cards;

namespace DeckApi.Formats;

/// <summary>
/// Data-driven description of a format, loaded from <c>Formats/Config/formats.json</c> (embedded
/// default) or from the file pointed by <c>Formats:ConfigPath</c>. Everything a format shares with the
/// others (sets, ban/suspension lists, numeric limits, unique allowlist) lives here; only genuinely
/// format-specific behaviour (sealed pool membership) is code, see <see cref="DeckValidator"/>.
///
/// This is meant to be replaced by the future card database API as source of truth: keep it behind
/// <see cref="IFormatCatalog"/>.
/// </summary>
public sealed class FormatDefinition
{
    public required string Code { get; init; }
    public required string Label { get; init; }
    public bool Hidden { get; init; }

    /// <summary>Codes also accepted on input for this format (case-insensitive), e.g. <c>no_unique</c>.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>Non-hero card count bounds. <c>null</c> max = unbounded.</summary>
    public int MinCards { get; init; }
    public int? MaxCards { get; init; }

    /// <summary>Exactly one hero required (all current formats).</summary>
    public bool RequireHero { get; init; } = true;

    /// <summary>Max distinct factions (NE ignored). <c>null</c> = no faction check.</summary>
    public int? MaxFactions { get; init; } = 1;

    /// <summary>Sets allowed. Empty = any set not explicitly forbidden.</summary>
    public string[] AllowedSets { get; init; } = [];
    public string[] ForbiddenSets { get; init; } = [];

    public bool AllowBanned { get; init; }
    public bool AllowSuspended { get; init; }

    /// <summary>Ban-list entries (see <see cref="CardMatcher"/>) on top of the shared lists.</summary>
    public string[] Banned { get; init; } = [];
    public string[] Suspended { get; init; } = [];

    /// <summary>Max copies of a family (<c>{FAC}_{NUM}</c>) across all rarities, sets and prints.</summary>
    public int? MaxCopiesPerFamily { get; init; }

    /// <summary>Max copies of a family for one given rarity (singleton: 1).</summary>
    public int? MaxCopiesPerRarity { get; init; }

    public int? MaxUniques { get; init; }
    public int? MaxRares { get; init; }
    public int? MaxExalted { get; init; }

    /// <summary>Each Unique reference at most once (standard/frontier).</summary>
    public bool UniqueSingleCopy { get; init; }

    /// <summary>
    /// Max uniques depending on the hero (singleton). Keys are the limit, values hero name fragments
    /// matched case-insensitively against the hero's English name. Unmatched hero = <see cref="MaxUniques"/>.
    /// </summary>
    public Dictionary<string, string[]>? UniqueLimitsByHero { get; init; }

    /// <summary>Embedded resource / file name listing the only Uniques allowed (frontier).</summary>
    public string? UniqueAllowlist { get; init; }

    /// <summary>Format-specific code hook. Currently only <c>sealed</c>.</summary>
    public string? Special { get; init; }

    /// <summary>
    /// Seasons, oldest first (e.g. Frontier S2, S3, S4). Each one is validated with its own definition (see
    /// <see cref="FormatSeason"/>). A deck legal in a season keeps that season in <c>valid_seasons</c>, even
    /// after a later season makes it illegal. Empty = the format has no seasons.
    /// </summary>
    public List<FormatSeason> Seasons { get; init; } = [];

    /// <summary>The season this definition validates, null for a format without seasons.</summary>
    [JsonIgnore] public FormatSeason? Season { get; internal set; }

    /// <summary>Key stored in <c>Deck.ValidSeasons</c>, e.g. <c>frontier:S4</c>.</summary>
    public static string SeasonKey(string formatCode, string seasonCode) => $"{formatCode}:{seasonCode}";

    /// <summary>Shared + format-specific entries.</summary>
    [JsonIgnore] public string[] EffectiveBanned { get; internal set; } = [];
    [JsonIgnore] public string[] EffectiveSuspended { get; internal set; } = [];
    [JsonIgnore] internal CardMatcher BannedMatcher { get; set; } = CardMatcher.Empty;
    [JsonIgnore] internal CardMatcher SuspendedMatcher { get; set; } = CardMatcher.Empty;
    [JsonIgnore] public HashSet<string>? UniqueAllowlistRefs { get; set; }

    public bool IsSetAllowed(string set, out bool forbidden)
    {
        forbidden = ForbiddenSets.Contains(set, StringComparer.Ordinal);
        if (forbidden) return false;
        return AllowedSets.Length == 0 || AllowedSets.Contains(set, StringComparer.Ordinal);
    }

    public int? UniqueLimitForHero(string? heroEnglishName)
    {
        if (UniqueLimitsByHero is null || heroEnglishName is null) return MaxUniques;
        var name = heroEnglishName.ToLowerInvariant();
        foreach (var (limit, fragments) in UniqueLimitsByHero.OrderBy(kv => int.Parse(kv.Key)))
        {
            if (fragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) return int.Parse(limit);
        }
        return MaxUniques;
    }
}

/// <summary>
/// One season of a format. Overrides only what changes between seasons: the unique allowlist (replaced) and
/// extra bans/suspensions (added to the shared and format lists). <c>Start</c>/<c>End</c> are informative and
/// pick the current season; a deck is only checked against a past season it could have been played in.
/// </summary>
public sealed class FormatSeason
{
    public required string Code { get; init; }
    public required string Label { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? UniqueAllowlist { get; init; }
    public string[] Banned { get; init; } = [];
    public string[] Suspended { get; init; } = [];

    public bool HasStarted(DateTimeOffset now) => Start is null || Start <= now;

    /// <summary>A deck created after the season ended was never played in it.</summary>
    public bool CouldHaveBeenPlayed(DateTimeOffset deckCreatedAt) => End is null || End >= deckCreatedAt;
}

/// <summary>
/// Matches a card against ban-list entries. Supported entry shapes:
/// <list type="bullet">
/// <item><c>ALT_CORE_B_AX_04_U_123</c> — exact reference;</item>
/// <item><c>BR_06</c> — whole family, every rarity, set and print (alt art included);</item>
/// <item><c>OR_27_R1</c> — family restricted to one rarity (<c>C</c>, <c>R1</c>, <c>R2</c>, <c>U</c>, <c>E</c>).</item>
/// </list>
/// </summary>
public sealed class CardMatcher
{
    public static readonly CardMatcher Empty = new([]);

    private readonly HashSet<string> _refs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _families = new(StringComparer.Ordinal);
    private readonly HashSet<string> _familyRarities = new(StringComparer.Ordinal);

    public CardMatcher(IEnumerable<string> entries)
    {
        foreach (var raw in entries)
        {
            var e = raw.Trim().ToUpperInvariant();
            if (e.Length == 0) continue;
            if (e.StartsWith("ALT_", StringComparison.Ordinal)) { _refs.Add(e); continue; }
            var parts = e.Split('_');
            if (parts.Length is < 2 or > 3 || !int.TryParse(parts[1], out var n))
                throw new InvalidDataException($"Invalid ban-list entry '{raw}'.");
            var family = $"{parts[0]}_{n:00}";
            if (parts.Length == 2) _families.Add(family);
            else _familyRarities.Add($"{family}_{(parts[2] == "R" ? "R1" : parts[2])}");
        }
    }

    public bool IsEmpty => _refs.Count == 0 && _families.Count == 0 && _familyRarities.Count == 0;

    public bool Matches(CardReference card) =>
        _refs.Contains(card.Value)
        || _refs.Contains(card.BoosterReference)
        || _families.Contains(card.Family)
        || _familyRarities.Contains($"{card.Family}_{card.RarityCode}");
}
