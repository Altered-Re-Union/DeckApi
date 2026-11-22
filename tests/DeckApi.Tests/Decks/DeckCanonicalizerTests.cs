using DeckApi.Data;
using DeckApi.Decks;

namespace DeckApi.Tests.Decks;

public class DeckCanonicalizerTests
{
    private static readonly DeckApi.Cards.CardCatalog Catalog = TestCards.LoadedCatalog();

    [Fact]
    public void Prints_become_canonical_lines_plus_overrides_and_round_trip()
    {
        List<DeckLine> sent =
        [
            new("ALT_CORE_P_AX_01_C", 1),   // promo hero
            new("ALT_CORE_A_AX_04_C", 2),   // alt art
            new("ALT_CORE_B_AX_04_C", 1),
            new("ALT_ALIZE_B_AX_06_C", 3),  // reprint
            new("ALT_CORE_B_AX_04_U_12", 1),
        ];

        var canonical = DeckCanonicalizer.Canonicalize(sent, null, null, Catalog);

        Assert.Equal(
            ["ALT_CORE_B_AX_01_C:1", "ALT_CORE_B_AX_04_C:3", "ALT_CORE_B_AX_06_C:3", "ALT_CORE_B_AX_04_U_12:1"],
            canonical.Lines.Select(l => $"{l.Reference}:{l.Quantity}"));
        Assert.Equal([new DeckLine("ALT_CORE_A_AX_04_C", 2)], canonical.Overrides!.Cards["ALT_CORE_B_AX_04_C"]);
        Assert.Equal([new DeckLine("ALT_ALIZE_B_AX_06_C", 3)], canonical.Overrides.Cards["ALT_CORE_B_AX_06_C"]);
        Assert.Equal("ALT_CORE_P_AX_01_C", DeckCanonicalizer.DisplayReference("ALT_CORE_B_AX_01_C", canonical.Overrides));

        Assert.Equal(sent.OrderBy(l => l.Reference), DeckCanonicalizer.Materialize(canonical.Lines, canonical.Overrides).OrderBy(l => l.Reference));
    }

    [Fact]
    public void Explicit_overrides_apply_on_canonical_lines_and_are_clamped()
    {
        var overrides = new AltArtOverrides
        {
            Cards = { ["ALT_CORE_B_AX_04_C"] = [new("ALT_CORE_A_AX_04_C", 5)] },
            Tokens = [new("ALT_CORE_A_BR_31_C", 1)],
        };
        var canonical = DeckCanonicalizer.Canonicalize([new("ALT_CORE_B_AX_04_C", 3)], overrides, null, Catalog);

        Assert.Equal([new DeckLine("ALT_CORE_A_AX_04_C", 3)], canonical.Overrides!.Cards["ALT_CORE_B_AX_04_C"]);
        Assert.Single(canonical.Overrides.Tokens);
    }

    [Fact]
    public void Base_art_can_be_pinned_through_explicit_overrides_only()
    {
        var pinBase = new AltArtOverrides { Cards = { ["ALT_CORE_B_AX_04_C"] = [new("ALT_CORE_B_AX_04_C", 2)] } };
        var pinned = DeckCanonicalizer.Canonicalize([new("ALT_CORE_B_AX_04_C", 3)], pinBase, null, Catalog);
        Assert.Equal([new DeckLine("ALT_CORE_B_AX_04_C", 2)], pinned.Overrides!.Cards["ALT_CORE_B_AX_04_C"]);
        Assert.Equal([new DeckLine("ALT_CORE_B_AX_04_C", 3)], DeckCanonicalizer.Materialize(pinned.Lines, pinned.Overrides));

        // A canonical line in the list alone is not a pin.
        Assert.Null(DeckCanonicalizer.Canonicalize([new("ALT_CORE_B_AX_04_C", 3)], null, null, Catalog).Overrides);
    }
}
