using DeckApi.Data;
using DeckApi.Decks;

namespace DeckApi.Tests.Decks;

public class DeckContentTests
{
    [Fact]
    public async Task Round_trips_through_deckfmt_and_computes_stats()
    {
        var catalog = TestCards.LoadedCatalog();
        var lines = TestCards.StandardAxDeck();
        lines.Add(new("ALT_CORE_A_AX_20_R1", 2));
        lines.Add(new("ALT_CORE_B_AX_04_U_1", 1));
        var deck = new Deck { Name = "t", Compressed = "" };

        DeckContent.Apply(deck, lines, await catalog.ResolveAsync(lines.Select(l => l.Reference), default));

        Assert.Null(deck.ExtraCards);
        Assert.Equal(lines.OrderBy(l => l.Reference), DeckContent.Read(deck).OrderBy(l => l.Reference));
        Assert.Equal(TestCards.AxHero, deck.HeroReference);
        Assert.Equal("AX", deck.HeroFaction);
        Assert.Equal(42, deck.CardCount);
        Assert.Equal((39, 2, 1, 0), (deck.ByRarity.C, deck.ByRarity.R, deck.ByRarity.U, deck.ByRarity.E));
        Assert.Contains("AX_04", deck.CardFamilies);
        Assert.Contains("ALT_CORE_B_AX_04_U_1", deck.CardReferences);
    }

    [Fact]
    public void Keeps_lines_deckfmt_cannot_encode()
    {
        // A set unknown to the Deckfmt config can't be encoded. (MUSUBI_40 used to land here too, with
        // v1's 5 number bits; v2 widened MUSUBI to 7 bits, so it is compressed now.)
        var lines = new List<DeckLine> { new("ALT_CORE_B_AX_04_C", 3), new("ALT_MUSUBI_B_AX_40_C", 1), new("ALT_NEWSET_B_AX_04_C", 1) };
        var deck = new Deck { Name = "t", Compressed = "" };
        DeckContent.Apply(deck, lines, null);

        Assert.Equal(["ALT_NEWSET_B_AX_04_C"], deck.ExtraCards!.Select(l => l.Reference));
        Assert.Equal(lines.OrderBy(l => l.Reference), DeckContent.Read(deck).OrderBy(l => l.Reference));
    }

    [Fact]
    public void Normalize_merges_duplicates() =>
        Assert.Equal([new DeckLine("A", 3), new DeckLine("B", 1)],
            DeckContent.Normalize([new("A", 1), new("B", 1), new("A", 2), new("C", 0)]));
}
