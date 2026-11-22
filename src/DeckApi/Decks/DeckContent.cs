using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Deckfmt;

namespace DeckApi.Decks;

/// <summary>Encodes/decodes a deck's card list to/from its stored form (Deckfmt + rare uncompressible lines).</summary>
public static class DeckContent
{
    /// <summary>Merges duplicate references (summing quantities) and drops non-positive quantities.</summary>
    public static List<DeckLine> Normalize(IEnumerable<DeckLine> lines) =>
        lines.Where(l => l.Quantity > 0)
            .GroupBy(l => l.Reference, StringComparer.Ordinal)
            .Select(g => new DeckLine(g.Key, g.Sum(l => l.Quantity)))
            .ToList();

    /// <summary>
    /// Writes content fields of <paramref name="deck"/>: compressed list, extra lines, reference/family
    /// arrays, hero and stats. <paramref name="resolved"/> may be null when the card catalog is unavailable
    /// (then no hero can be detected, like the old API when its card fetch failed).
    /// </summary>
    public static void Apply(Deck deck, IReadOnlyList<DeckLine> lines, IReadOnlyDictionary<string, ResolvedCard>? resolved)
    {
        var encodable = new List<CardRefQty>();
        var extra = new List<DeckLine>();
        foreach (var line in lines)
        {
            if (IsLosslesslyEncodable(line)) encodable.Add(new CardRefQty(line.Quantity, line.Reference));
            else extra.Add(line);
        }

        deck.Compressed = Encode(encodable);
        deck.ExtraCards = extra.Count > 0 ? extra : null;
        deck.CardReferences = lines.Select(l => l.Reference).Distinct().Order(StringComparer.Ordinal).ToArray();
        deck.CardFamilies = lines
            .Select(l => CardReference.TryParse(l.Reference, out var r) ? r.Family : null)
            .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();

        var heroLine = resolved is null ? null : lines.FirstOrDefault(l => resolved.GetValueOrDefault(l.Reference)?.IsHero == true);
        deck.HeroReference = heroLine?.Reference;
        deck.HeroFaction = heroLine is null ? null : heroLine.Reference.Split('_') is { Length: > 3 } p ? p[3] : null;

        var counts = new RarityCounts();
        foreach (var line in lines)
        {
            if (resolved?.GetValueOrDefault(line.Reference)?.IsHero == true) continue;
            var bucket = CardReference.TryParse(line.Reference, out var r) ? r.RarityBucket : "C";
            switch (bucket)
            {
                case "R": counts.R += line.Quantity; break;
                case "U": counts.U += line.Quantity; break;
                case "E": counts.E += line.Quantity; break;
                default: counts.C += line.Quantity; break;
            }
        }
        deck.ByRarity = counts;
        deck.CardCount = counts.C + counts.R + counts.U + counts.E;
    }

    /// <summary>Full card list (hero included), compressed part first.</summary>
    public static List<DeckLine> Read(Deck deck)
    {
        var lines = Decode(deck.Compressed);
        if (deck.ExtraCards is { Count: > 0 } extra) lines.AddRange(extra);
        return lines;
    }

    public static string Encode(IReadOnlyList<CardRefQty> cards)
    {
        var writer = new BitstreamWriter();
        EncodableDeck.FromList(cards).Encode(writer);
        return Convert.ToBase64String(writer.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static List<DeckLine> Decode(string compressed)
    {
        if (string.IsNullOrEmpty(compressed)) return [];
        var text = DeckfmtCodec.DecodeList(compressed);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2))
            .Select(p => new DeckLine(p[1], int.Parse(p[0])))
            .ToList();
    }

    /// <summary>True when the reference survives a Deckfmt round-trip unchanged (known set, number fits, quantity ≤ 65).</summary>
    public static bool IsLosslesslyEncodable(DeckLine line)
    {
        if (line.Quantity is < 1 or > 65) return false;
        try
        {
            return EncodableCard.FromId(line.Reference).AsCardId() == line.Reference
                   && Decode(Encode([new CardRefQty(line.Quantity, line.Reference)])) is [var back]
                   && back.Reference == line.Reference;
        }
        catch (Exception e) when (e is ArgumentException or EncodingException or DecodingException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}
