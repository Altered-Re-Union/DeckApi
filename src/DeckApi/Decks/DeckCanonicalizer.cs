using DeckApi.Cards;
using DeckApi.Data;

namespace DeckApi.Decks;

public sealed record CanonicalDeck(List<DeckLine> Lines, AltArtOverrides? Overrides);

/// <summary>
/// A deck is stored by gameplay identity: one canonical reference per family + rarity
/// (<see cref="ICardCatalog.CanonicalReference"/>; Uniques keep their set and serial), so the set/print a player
/// picked never affects legality, "decks containing card X" searches or copy counts. Which print to show is a
/// cosmetic choice kept in <see cref="AltArtOverrides"/> (keyed by canonical reference, highest priority over the
/// player's global preferences, ownership still checked by AlteredOwnership).
///
/// An override may also name the canonical print itself, to pin the base art over the player's global preferences
/// (only through <c>altArtOverrides</c>: a canonical line in the list is never a pin).
///
/// Old clients keep working: they send and read concrete prints. <see cref="Canonicalize"/> turns the prints they
/// send into canonical lines + overrides, <see cref="Materialize"/> turns them back into the prints they expect.
/// </summary>
public static class DeckCanonicalizer
{
    /// <param name="lines">Deck lines as sent (any print).</param>
    /// <param name="explicitOverrides">Overrides sent with the lines (new clients), applied after the prints found in the lines.</param>
    /// <param name="tokens">Token overrides to keep.</param>
    public static CanonicalDeck Canonicalize(IReadOnlyList<DeckLine> lines, AltArtOverrides? explicitOverrides, List<DeckLine>? tokens, ICardCatalog catalog)
    {
        var order = new List<string>();
        var quantities = new Dictionary<string, int>(StringComparer.Ordinal);
        var prints = new Dictionary<string, List<DeckLine>>(StringComparer.Ordinal);

        string Canonical(string reference) => catalog.CanonicalReference(reference) ?? reference;

        foreach (var line in lines.Where(l => l.Quantity > 0))
        {
            var canonical = Canonical(line.Reference);
            if (!quantities.ContainsKey(canonical)) order.Add(canonical);
            quantities[canonical] = quantities.GetValueOrDefault(canonical) + line.Quantity;
            if (canonical != line.Reference) AddPrint(prints, canonical, line);
        }

        if (explicitOverrides is not null)
        {
            foreach (var (key, replacements) in explicitOverrides.Cards)
            {
                var canonical = Canonical(key);
                // An explicit override may name the canonical print itself: "base art for this deck", pinned
                // over the player's global preferences. Plain canonical lines in the list never pin anything.
                foreach (var replacement in replacements.Where(r => r.Quantity > 0))
                    AddPrint(prints, canonical, replacement);
            }
        }

        var cards = new Dictionary<string, List<DeckLine>>(StringComparer.Ordinal);
        foreach (var (canonical, chosen) in prints)
        {
            if (!quantities.TryGetValue(canonical, out var available)) continue;
            var kept = new List<DeckLine>();
            foreach (var group in chosen.GroupBy(p => p.Reference, StringComparer.Ordinal))
            {
                var quantity = Math.Min(group.Sum(p => p.Quantity), available);
                if (quantity <= 0) break;
                kept.Add(new DeckLine(group.Key, quantity));
                available -= quantity;
            }
            if (kept.Count > 0) cards[canonical] = kept;
        }

        var finalTokens = explicitOverrides?.Tokens ?? tokens ?? [];
        var overrides = cards.Count == 0 && finalTokens.Count == 0 ? null : new AltArtOverrides { Cards = cards, Tokens = finalTokens };
        return new CanonicalDeck(order.Select(c => new DeckLine(c, quantities[c])).ToList(), overrides);
    }

    /// <summary>Concrete prints to show (old API shape): overridden copies as their chosen print, the rest canonical.</summary>
    public static List<DeckLine> Materialize(IReadOnlyList<DeckLine> canonicalLines, AltArtOverrides? overrides)
    {
        var result = new List<DeckLine>();
        foreach (var line in canonicalLines)
        {
            var remaining = line.Quantity;
            if (overrides?.Cards.TryGetValue(line.Reference, out var chosen) == true)
            {
                foreach (var print in chosen)
                {
                    var quantity = Math.Min(print.Quantity, remaining);
                    if (quantity <= 0) break;
                    result.Add(new DeckLine(print.Reference, quantity));
                    remaining -= quantity;
                }
            }
            if (remaining > 0) result.Add(line with { Quantity = remaining });
        }
        return DeckContent.Normalize(result);
    }

    /// <summary>The print shown for a canonical reference (first override, else itself) — e.g. the hero.</summary>
    public static string DisplayReference(string canonical, AltArtOverrides? overrides) =>
        overrides?.Cards.TryGetValue(canonical, out var chosen) == true && chosen.Count > 0 ? chosen[0].Reference : canonical;

    private static void AddPrint(Dictionary<string, List<DeckLine>> prints, string canonical, DeckLine print)
    {
        if (!prints.TryGetValue(canonical, out var list)) prints[canonical] = list = [];
        list.Add(print);
    }
}
