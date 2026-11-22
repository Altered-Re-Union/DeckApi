using System.Text.Json.Serialization;

namespace DeckApi.Cards;

/// <summary>Subset of uniques-search-api's <c>CardV2</c> the deck API needs (display + validation).</summary>
public sealed class CatalogCard
{
    public required string Reference { get; init; }

    /// <summary>Locale (e.g. <c>fr_FR</c>, <c>en_US</c>) → name.</summary>
    public Dictionary<string, string> Name { get; init; } = new();
    public string? Artist { get; init; }
    public CodeName? Set { get; init; }
    public List<RefName> CardSubTypes { get; init; } = [];
    public int? MainCost { get; init; }
    public int? RecallCost { get; init; }
    public int? ForestPower { get; init; }
    public int? MountainPower { get; init; }
    public int? OceanPower { get; init; }
    public CodeName? Faction { get; init; }
    public Dictionary<string, string> MainEffect { get; init; } = new();
    public Dictionary<string, string> EchoEffect { get; init; } = new();
    public RefName? CardType { get; init; }
    public RefOnly? Rarity { get; init; }
    public string? Product { get; init; }
    public bool? IsBanned { get; init; }
    public bool? IsSuspended { get; init; }

    [JsonIgnore] public bool IsHero => CardType?.Reference?.Contains("HERO", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Localized lookup accepting either a short locale (<c>fr</c>) or a full one (<c>fr_FR</c>), falling back
    /// to French then English (the old API's fallback was French).
    /// </summary>
    public static string? Localize(IReadOnlyDictionary<string, string>? values, string? locale)
    {
        if (values is null || values.Count == 0) return null;
        static string? Find(IReadOnlyDictionary<string, string> v, string l)
        {
            if (v.TryGetValue(l, out var exact)) return exact;
            foreach (var (k, val) in v)
                if (k.StartsWith(l + "_", StringComparison.OrdinalIgnoreCase) || k.Equals(l, StringComparison.OrdinalIgnoreCase))
                    return val;
            return null;
        }
        return (locale is null ? null : Find(values, locale.Split('-', '_')[0].ToLowerInvariant()))
               ?? Find(values, "fr") ?? Find(values, "en") ?? values.Values.First();
    }

    public sealed record CodeName(string? Code, string? Name, string? Reference = null);
    public sealed record RefName(string? Reference, Dictionary<string, string>? Name);
    public sealed record RefOnly(string? Reference, string? Name);
}
