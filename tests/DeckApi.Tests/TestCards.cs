using DeckApi.Cards;
using DeckApi.Data;

namespace DeckApi.Tests;

/// <summary>Small synthetic non-unique catalog: one hero and plain characters per faction.</summary>
public static class TestCards
{
    public const string AxHero = "ALT_CORE_B_AX_01_C";
    public const string BrHero = "ALT_CORE_B_BR_01_C";

    public static List<CatalogCard> Catalog()
    {
        var cards = new List<CatalogCard>();
        foreach (var faction in new[] { "AX", "BR", "LY", "MU", "OR", "YZ" })
        {
            cards.Add(Card($"ALT_CORE_B_{faction}_01_C", faction, "HERO", $"Hero {faction}"));
            cards.Add(Card($"ALT_CORE_P_{faction}_01_C", faction, "HERO", $"Hero {faction}"));
            for (var n = 4; n <= 30; n++)
            {
                foreach (var rarity in new[] { "C", "R1", "R2" })
                    cards.Add(Card($"ALT_CORE_B_{faction}_{n:00}_{rarity}", faction, "CHARACTER", $"Card {faction} {n}"));
                cards.Add(Card($"ALT_CORE_A_{faction}_{n:00}_R1", faction, "CHARACTER", $"Card {faction} {n}"));
                // Same family reprinted in a later set.
                cards.Add(Card($"ALT_ALIZE_B_{faction}_{n:00}_C", faction, "CHARACTER", $"Card {faction} {n}"));
            }
        }
        cards.Add(Card("ALT_CORE_B_NE_01_C", "NE", "TOKEN_MANA", "Mana Orb"));
        cards.Add(Card("ALT_FUGUE_B_AX_140_C", "AX", "CHARACTER", "Future card"));
        cards.Add(Card("ALT_FUGUE_B_AX_05_C", "AX", "CHARACTER", "Card AX 5")); // FUGUE reprint of a CORE card
        cards.Add(Card("ALT_CORE_A_AX_04_C", "AX", "CHARACTER", "Card AX 4"));
        cards.Add(Card("ALT_MUSUBI_B_AX_40_C", "AX", "CHARACTER", "Musubi high number"));
        cards.Add(Card("ALT_CORE_B_BR_06_C", "BR", "CHARACTER", "Suspended card"));
        return cards;
    }

    public static CatalogCard Card(string reference, string faction, string type, string name) => new()
    {
        Reference = reference,
        Name = new() { ["en_US"] = name, ["fr_FR"] = name + " (fr)" },
        Faction = new CatalogCard.CodeName(faction, faction),
        CardType = new CatalogCard.RefName(type, new() { ["en_US"] = type }),
        MainCost = 2,
        RecallCost = 1,
        MainEffect = new() { ["en_US"] = "Effect of " + name },
    };

    public static CardCatalog LoadedCatalog()
    {
        var catalog = new CardCatalog(UniqueFactionRanges.LoadEmbedded(), null!, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CardCatalog>.Instance);
        catalog.Replace(Catalog(), DateTimeOffset.UtcNow);
        return catalog;
    }

    /// <summary>Legal standard AX deck: hero + 13 families × 3 commons = 39 cards.</summary>
    public static List<DeckLine> StandardAxDeck()
    {
        var lines = new List<DeckLine> { new(AxHero, 1) };
        for (var n = 4; n <= 16; n++) lines.Add(new DeckLine($"ALT_CORE_B_AX_{n:00}_C", 3));
        return lines;
    }
}
