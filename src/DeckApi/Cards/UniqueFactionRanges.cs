using System.Text.Json;

namespace DeckApi.Cards;

/// <summary>
/// Resolves the real faction of a Unique from its serial, without calling any card API.
///
/// A Unique family (e.g. CORE <c>AX_04</c>) is printed in its home faction and in one
/// out-of-faction (OOF) faction; each faction owns disjoint, inclusive serial windows that together
/// cover <c>1..N</c>. Tables come from altered-draft (<c>src/lib/data/factionRanges/{SET}.json</c>,
/// generated from CardsData <c>faction_ranges_&lt;SET&gt;.csv</c>), shape
/// <c>{ "AX_04": { "AX": [[1,660],...], "MU": [[661,1320],...] } }</c>.
///
/// Unknown set / family / serial outside every window returns <c>null</c>: callers must NOT fall back to
/// the reference's faction prefix (CYCLONE <c>OR_68</c> is entirely YZ).
/// </summary>
public sealed class UniqueFactionRanges
{
    // set -> family -> sorted windows (start, end, faction)
    private readonly Dictionary<string, Dictionary<string, (int Start, int End, string Faction)[]>> _tables;

    private UniqueFactionRanges(Dictionary<string, Dictionary<string, (int, int, string)[]>> tables) => _tables = tables;

    public IReadOnlyCollection<string> Sets => _tables.Keys;

    public static UniqueFactionRanges LoadEmbedded()
    {
        var assembly = typeof(UniqueFactionRanges).Assembly;
        var tables = new Dictionary<string, Dictionary<string, (int, int, string)[]>>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            const string marker = ".Cards.FactionRanges.";
            var idx = name.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0 || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
            var set = name[(idx + marker.Length)..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(name)!;
            tables[set] = Parse(stream);
        }
        return new UniqueFactionRanges(tables);
    }

    internal static Dictionary<string, (int, int, string)[]> Parse(Stream json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int[][]>>>(json)
                  ?? throw new InvalidDataException("Empty faction range table.");
        var result = new Dictionary<string, (int, int, string)[]>(StringComparer.Ordinal);
        foreach (var (family, byFaction) in raw)
        {
            result[family] = byFaction
                .SelectMany(kv => kv.Value.Select(w => (w[0], w[1], kv.Key)))
                .OrderBy(w => w.Item1)
                .ToArray();
        }
        return result;
    }

    /// <summary>True when a table exists for this set (so a null answer means "unknown serial").</summary>
    public bool CoversSet(string set) => _tables.ContainsKey(set);

    public string? FactionOf(CardReference reference)
    {
        if (!reference.IsUnique || reference.Serial is not { } serial) return null;
        if (!_tables.TryGetValue(reference.Set, out var families)) return null;
        if (!families.TryGetValue(reference.Family, out var windows)) return null;

        // Binary search on the start of the windows.
        int lo = 0, hi = windows.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var w = windows[mid];
            if (serial < w.Start) hi = mid - 1;
            else if (serial > w.End) lo = mid + 1;
            else return w.Faction;
        }
        return null;
    }
}
