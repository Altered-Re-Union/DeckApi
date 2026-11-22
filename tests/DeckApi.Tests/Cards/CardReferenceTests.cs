using DeckApi.Cards;

namespace DeckApi.Tests.Cards;

public class CardReferenceTests
{
    [Theory]
    [InlineData("ALT_CORE_B_AX_04_C", "CORE", 'B', "AX_04", Rarity.Common, null)]
    [InlineData("ALT_COREKS_A_AX_4_R2", "COREKS", 'A', "AX_04", Rarity.Rare2, null)]
    [InlineData("ALT_ALIZE_P_AX_04_R1", "ALIZE", 'P', "AX_04", Rarity.Rare1, null)]
    [InlineData("ALT_EOLE_B_OR_109_U_374", "EOLE", 'B', "OR_109", Rarity.Unique, 374)]
    [InlineData("ALT_DUSTER_B_LY_98_E", "DUSTER", 'B', "LY_98", Rarity.Exalted, null)]
    public void Parses_family_across_sets_prints_and_rarities(string value, string set, char product, string family, Rarity rarity, int? serial)
    {
        Assert.True(CardReference.TryParse(value, out var r));
        Assert.Equal(set, r.Set);
        Assert.Equal(product, r.Product);
        Assert.Equal(family, r.Family);
        Assert.Equal(rarity, r.Rarity);
        Assert.Equal(serial, r.Serial);
    }

    [Theory]
    [InlineData("ALT_CORE_B_AX_04_U")]
    [InlineData("ALT_CORE_B_AX_04_Z")]
    [InlineData("CORE_B_AX_04_C")]
    [InlineData("alt_core_b_ax_04_c")]
    public void Rejects_invalid(string value) => Assert.False(CardReference.TryParse(value, out _));

    [Fact]
    public void Booster_reference_keeps_padding_and_serial()
    {
        Assert.Equal("ALT_COREKS_B_NE_1_C", CardReference.Parse("ALT_COREKS_A_NE_1_C").BoosterReference);
        Assert.Equal("ALT_CORE_B_AX_04_U_12", CardReference.Parse("ALT_CORE_B_AX_04_U_12").BoosterReference);
    }
}

public class UniqueFactionRangesTests
{
    private static readonly UniqueFactionRanges Ranges = UniqueFactionRanges.LoadEmbedded();

    [Theory]
    [InlineData("ALT_CORE_B_AX_04_U_1", "AX")]
    [InlineData("ALT_CORE_B_AX_04_U_661", "MU")]
    [InlineData("ALT_CORE_B_AX_04_U_8980", "MU")]
    [InlineData("ALT_EOLE_B_AX_111_U_100", "YZ")] // documented in altered-draft ROADMAP
    [InlineData("ALT_EOLE_B_AX_111_U_400", "AX")]
    [InlineData("ALT_CYCLONE_B_OR_68_U_5", "YZ")] // whole family is YZ: never fall back to the prefix
    public void Resolves_real_faction(string reference, string faction) =>
        Assert.Equal(faction, Ranges.FactionOf(CardReference.Parse(reference)));

    [Theory]
    [InlineData("ALT_CORE_B_AX_04_U_8981")] // past the end of the table
    [InlineData("ALT_CORE_B_AX_99_U_1")]    // unknown family
    [InlineData("ALT_FUGUE_B_AX_04_U_1")]   // set without table
    [InlineData("ALT_CORE_B_AX_04_C")]      // not a unique
    public void Unknown_is_null(string reference) => Assert.Null(Ranges.FactionOf(CardReference.Parse(reference)));

    [Fact]
    public void Covers_released_unique_sets() =>
        Assert.Equal(["ALIZE", "BISE", "CORE", "COREKS", "CYCLONE", "DUSTER", "EOLE"], Ranges.Sets.Order());
}
