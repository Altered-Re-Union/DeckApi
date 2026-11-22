using Microsoft.Extensions.Caching.Memory;

namespace DeckApi.Cards;

public sealed record FamilyInfo(string Family, string? CardType, bool IsHero, IReadOnlyDictionary<string, string> Name);

/// <summary>
/// What validation needs to know about one deck line. <see cref="Sets"/> = every set the card's gameplay
/// identity (family + rarity) is printed in, so set legality is "printed in at least one allowed set"; a
/// Unique only exists in its own set.
/// </summary>
public sealed record ResolvedCard(CardReference? Reference, bool Known, bool IsHero, string? Faction, string? EnglishName,
    IReadOnlyCollection<string>? Sets = null)
{
    public IReadOnlyCollection<string> PrintedIn => Sets ?? (Reference is null ? [] : [Reference.Set]);
}

public sealed class CatalogUnavailableException() : Exception("Card catalog is not loaded yet (uniques-search-api unreachable and no snapshot).");

public interface ICardCatalog
{
    bool IsLoaded { get; }
    DateTimeOffset? LoadedAt { get; }
    FamilyInfo? GetFamily(string family);
    CatalogCard? GetPrint(string reference);

    /// <summary>
    /// Canonical reference of a card's gameplay identity: the booster print of the first set (in
    /// <see cref="CardCatalog.CanonicalSetOrder"/>) printing that family + rarity. Uniques keep their set and serial
    /// (a serial only identifies a Unique within its set), as a booster print. Null when unknown / catalog not loaded.
    /// </summary>
    string? CanonicalReference(string reference);
    IReadOnlyCollection<string> FamiliesMatchingName(string fragment);

    /// <summary>Validation view of each reference. Throws <see cref="CatalogUnavailableException"/>.</summary>
    Task<IReadOnlyDictionary<string, ResolvedCard>> ResolveAsync(IEnumerable<string> references, CancellationToken ct);

    /// <summary>Full card data for display; uniques are fetched (and cached) from uniques-search-api. Never throws.</summary>
    Task<IReadOnlyDictionary<string, CatalogCard>> GetCardsAsync(IEnumerable<string> references, CancellationToken ct);
}

/// <summary>
/// In-memory card reference. Non-uniques (the whole ~3.5k print catalog) are held in memory and
/// refreshed by <see cref="CardCatalogRefresher"/>; Uniques are resolved locally with
/// <see cref="UniqueFactionRanges"/> (they are always characters), with an API fallback only for sets
/// that have no range table yet.
/// </summary>
public sealed class CardCatalog(UniqueFactionRanges ranges, IServiceScopeFactory scopes, IMemoryCache cache, ILogger<CardCatalog> logger) : ICardCatalog
{
    /// <summary>Set priority for canonical prints: main sets in release order, then any other set (promos…) by name.</summary>
    public static readonly string[] CanonicalSetOrder = ["CORE", "COREKS", "ALIZE", "BISE", "CYCLONE", "DUSTER", "EOLE", "FUGUE"];

    private sealed record Identity(string Canonical, string[] Sets);

    private sealed record Snapshot(
        Dictionary<string, CatalogCard> ByReference,
        Dictionary<string, FamilyInfo> ByFamily,
        Dictionary<string, Identity> ByFamilyRarity,
        DateTimeOffset LoadedAt);

    private static string IdentityKey(CardReference r) => $"{r.Family}_{r.RarityCode}";

    private static int SetRank(string set)
    {
        var i = Array.IndexOf(CanonicalSetOrder, set);
        return i >= 0 ? i : CanonicalSetOrder.Length;
    }

    private volatile Snapshot? _snapshot;

    public bool IsLoaded => _snapshot is not null;
    public DateTimeOffset? LoadedAt => _snapshot?.LoadedAt;

    public void Replace(IReadOnlyCollection<CatalogCard> cards, DateTimeOffset loadedAt)
    {
        var byRef = new Dictionary<string, CatalogCard>(StringComparer.Ordinal);
        var byFamily = new Dictionary<string, FamilyInfo>(StringComparer.Ordinal);
        // Prefer booster commons as the family's representative (name/type), then anything.
        foreach (var card in cards.OrderBy(c => c.Reference.Contains("_B_") ? 0 : 1).ThenBy(c => c.Reference.EndsWith("_C") ? 0 : 1))
        {
            byRef[card.Reference] = card;
            if (CardReference.TryParse(card.Reference, out var parsed) && !byFamily.ContainsKey(parsed.Family))
                byFamily[parsed.Family] = new FamilyInfo(parsed.Family, card.CardType?.Reference, card.IsHero, card.Name);
        }
        var byIdentity = new Dictionary<string, Identity>(StringComparer.Ordinal);
        foreach (var group in byRef.Values
                     .Select(c => CardReference.TryParse(c.Reference, out var r) && !r.IsUnique ? r : null)
                     .OfType<CardReference>()
                     .GroupBy(IdentityKey))
        {
            var canonical = group
                .OrderBy(r => r.Product == 'B' ? 0 : 1)
                .ThenBy(r => SetRank(r.Set))
                .ThenBy(r => r.Set, StringComparer.Ordinal)
                .ThenBy(r => r.Value, StringComparer.Ordinal)
                .First();
            byIdentity[group.Key] = new Identity(canonical.Value, group.Select(r => r.Set).Distinct().Order(StringComparer.Ordinal).ToArray());
        }
        _snapshot = new Snapshot(byRef, byFamily, byIdentity, loadedAt);
    }

    public FamilyInfo? GetFamily(string family) => _snapshot?.ByFamily.GetValueOrDefault(family);

    public CatalogCard? GetPrint(string reference) => _snapshot?.ByReference.GetValueOrDefault(reference);

    public string? CanonicalReference(string reference)
    {
        var snapshot = _snapshot;
        if (snapshot is null || !CardReference.TryParse(reference, out var r)) return null;
        if (r.IsUnique) return r.BoosterReference;
        return snapshot.ByFamilyRarity.GetValueOrDefault(IdentityKey(r))?.Canonical;
    }

    public IReadOnlyCollection<string> FamiliesMatchingName(string fragment)
    {
        var snapshot = _snapshot;
        if (snapshot is null || string.IsNullOrWhiteSpace(fragment)) return [];
        return snapshot.ByFamily.Values
            .Where(f => f.Name.Values.Any(n => n.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Family)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, ResolvedCard>> ResolveAsync(IEnumerable<string> references, CancellationToken ct)
    {
        var snapshot = _snapshot ?? throw new CatalogUnavailableException();
        var result = new Dictionary<string, ResolvedCard>(StringComparer.Ordinal);
        var needApi = new List<CardReference>();

        foreach (var value in references.Distinct())
        {
            if (!CardReference.TryParse(value, out var r))
            {
                result[value] = new ResolvedCard(null, false, false, null, null);
                continue;
            }

            var family = snapshot.ByFamily.GetValueOrDefault(r.Family);
            var englishName = family is null ? null : CatalogCard.Localize(family.Name, "en");

            if (r.IsUnique)
            {
                var faction = ranges.FactionOf(r);
                if (faction is not null) result[value] = new ResolvedCard(r, true, false, faction, englishName);
                else if (ranges.CoversSet(r.Set)) result[value] = new ResolvedCard(r, false, false, null, englishName);
                else needApi.Add(r); // set without range table yet (e.g. a brand new set)
                continue;
            }

            var sets = snapshot.ByFamilyRarity.GetValueOrDefault(IdentityKey(r))?.Sets;
            if (snapshot.ByReference.TryGetValue(value, out var print))
            {
                result[value] = new ResolvedCard(r, true, print.IsHero, print.Faction?.Code ?? r.Faction, englishName, sets);
            }
            else if (family is not null && sets is not null)
            {
                // Print not in the catalog (e.g. a promo/alt-art variant missing from CardsData) but the
                // gameplay identity exists: accept it, the faction is the reference's.
                result[value] = new ResolvedCard(r, true, family.IsHero, r.Faction, englishName, sets);
            }
            else
            {
                result[value] = new ResolvedCard(r, false, false, null, null);
            }
        }

        if (needApi.Count > 0)
        {
            var fetched = await GetCardsAsync(needApi.Select(r => r.Value), ct);
            foreach (var r in needApi)
            {
                result[r.Value] = fetched.TryGetValue(r.Value, out var card)
                    ? new ResolvedCard(r, true, false, card.Faction?.Code, CatalogCard.Localize(card.Name, "en"))
                    : new ResolvedCard(r, false, false, null, null);
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, CatalogCard>> GetCardsAsync(IEnumerable<string> references, CancellationToken ct)
    {
        var snapshot = _snapshot;
        var result = new Dictionary<string, CatalogCard>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var value in references.Distinct())
        {
            if (snapshot?.ByReference.TryGetValue(value, out var print) == true) result[value] = print;
            else if (cache.TryGetValue(CacheKey(value), out CatalogCard? cached) && cached is not null) result[value] = cached;
            else missing.Add(value);
        }

        if (missing.Count == 0) return result;
        try
        {
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<UniquesSearchClient>();
            foreach (var card in await client.FetchByReferencesAsync(missing, ct))
            {
                cache.Set(CacheKey(card.Reference), card, TimeSpan.FromHours(1));
                result[card.Reference] = card;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogWarning(e, "uniques-search-api lookup failed for {Count} references", missing.Count);
        }
        return result;
    }

    private static string CacheKey(string reference) => "card:" + reference;
}
