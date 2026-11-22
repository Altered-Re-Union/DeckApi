using DeckApi.Deckfmt;
using Xunit;

namespace DeckApi.Tests.Deckfmt;

public class DeckfmtTests
{
    [Theory]
    [InlineData(
        "1 ALT_CORE_B_YZ_03_C\n3 ALT_CORE_B_BR_16_R2\n2 ALT_CORE_B_YZ_04_C\n3 ALT_CORE_B_YZ_07_R1\n1 ALT_CORE_B_BR_10_R2\n1 ALT_CORE_B_MU_08_R2\n3 ALT_CORE_B_YZ_06_C\n2 ALT_CORE_B_YZ_11_C\n1 ALT_CORE_B_YZ_12_C\n3 ALT_CORE_B_YZ_14_C\n3 ALT_CORE_B_BR_25_R2\n3 ALT_CORE_B_YZ_19_C\n1 ALT_CORE_B_BR_28_R2\n3 ALT_CORE_B_MU_25_R2\n3 ALT_CORE_B_YZ_21_C\n3 ALT_CORE_B_YZ_22_C\n2 ALT_CORE_B_YZ_24_C\n1 ALT_CORE_B_YZ_26_C\n1 ALT_CORE_B_YZ_25_C",
        "IBAk3gx0hLhB8Omio4RPhhcWHjB8cOmZ8mGnJ4zPlR8sLmA80HmQ")]
    [InlineData("1 ALT_CORE_B_YZ_02_C\n3 ALT_CORE_B_LY_28_C\n2 ALT_CORE_B_NE_1_C", "IBAg3gh24LwQ")]
    [InlineData("55 ALT_CORE_B_MU_25_R1", "IBAgTTGU")]
    [InlineData("", "IAA")]
    // Same vectors as altered-bga-api's DeckfmtTests and alteredcore-website's tournament-reports/tests/DeckfmtTest.php.
    [InlineData("1 ALT_MUSUBI_B_BR_74_R1", "IBEQWpRA")]
    [InlineData("1 ALT_EOLETOP_A_YZ_105_C", "IBFAVbSA")]
    public void EncodeList_ProducesExpectedOutput(string input, string expectedBase64)
    {
        var encoded = DeckfmtCodec.EncodeList(input);
        Assert.Equal(expectedBase64, encoded);
    }

    [Theory]
    [InlineData("IBAk3gx0hLhB8Omio4RPhhcWHjB8cOmZ8mGnJ4zPlR8sLmA80HmQ")]
    [InlineData("IBAg3gh24LwQ")]
    [InlineData("IBAgTTGU")]
    [InlineData("IAA")]
    public void DecodeList_RoundTrips(string encoded)
    {
        var decoded = DeckfmtCodec.DecodeList(encoded);
        var reEncoded = DeckfmtCodec.EncodeList(decoded);
        Assert.Equal(encoded, reEncoded);
    }

    [Theory]
    // v1 blobs (leading "E") must keep decoding to the same list as their v2 counterpart (leading "I").
    [InlineData("EBAk3gx0hLhB8Omio4RPhhcWHjB8cOmZ8mGnJ4zPlR8sLmA80HmQ", "IBAk3gx0hLhB8Omio4RPhhcWHjB8cOmZ8mGnJ4zPlR8sLmA80HmQ")]
    [InlineData("EBAg3gh24LwQ", "IBAg3gh24LwQ")]
    [InlineData("EBAgTTGU", "IBAgTTGU")]
    [InlineData("EAA", "IAA")]
    public void DecodeList_StillDecodesV1Blobs(string v1Encoded, string v2Encoded) =>
        Assert.Equal(DeckfmtCodec.DecodeList(v2Encoded), DeckfmtCodec.DecodeList(v1Encoded));

    [Fact]
    public void DecodeList_V1MusubiBlobKeepsItsFiveBitNumber() =>
        // "1 ALT_MUSUBI_B_LY_06_C" as v1 encoded it (MUSUBI number on 5 bits; v2 widened it to 7).
        Assert.Equal("1 ALT_MUSUBI_B_LY_06_C", DeckfmtCodec.DecodeList("EBEQWzAA"));

    [Theory]
    [InlineData("ALT_MUSUBI_B_BR_74_R1")]
    [InlineData("ALT_EOLETOP_A_YZ_105_C")]
    [InlineData("ALT_EOLEOP_P_MU_122_R1")]
    public void RealCardId_RoundTrips(string cardId) =>
        Assert.Equal($"1 {cardId}", DeckfmtCodec.DecodeList(DeckfmtCodec.EncodeList($"1 {cardId}")));

    [Fact]
    public void Config_LatestIsV2_AndV1IsFrozen()
    {
        Assert.Equal(2, DeckfmtConfig.Latest);
        Assert.Equal(5, DeckfmtConfig.For(1).SetByCode("MUSUBI").NumberInFactionBits);
        Assert.Equal(7, DeckfmtConfig.For(2).SetByCode("MUSUBI").NumberInFactionBits);
    }

    [Fact]
    public void DecodeList_ReturnsExpectedDecklist()
    {
        var encoded = "IBAg3gh24LwQ";
        var decoded = DeckfmtCodec.DecodeList(encoded);
        Assert.Contains("ALT_CORE_B_YZ_02_C", decoded);
        Assert.Contains("ALT_CORE_B_LY_28_C", decoded);
        // Core's own NE_1 (Mana Convergence) is zero-padded, unlike CoreKS' --
        // see EncodableCard.AsCardId().
        Assert.Contains("ALT_CORE_B_NE_01_C", decoded);
    }

    [Fact]
    public void EncodeList_HandlesExtendedQuantity()
    {
        var list = "55 ALT_CORE_B_MU_25_R1";
        var encoded = DeckfmtCodec.EncodeList(list);
        var decoded = DeckfmtCodec.DecodeList(encoded);
        Assert.Contains("55 ALT_CORE_B_MU_25_R1", decoded);
    }

    [Fact]
    public void EncodeList_ThrowsOnQuantityAbove65()
    {
        var list = "66 ALT_CORE_B_AX_11_R1";
        Assert.Throws<EncodingException>(() => DeckfmtCodec.EncodeList(list));
    }

    [Fact]
    public void EncodeList_ThrowsOnInvalidCardId()
    {
        var list = "1 INVALID_CARD_ID";
        Assert.Throws<ArgumentException>(() => DeckfmtCodec.EncodeList(list));
    }

    [Fact]
    public void EncodeList_ThrowsOnUniqueBeyond16Bit()
    {
        var list = "1 ALT_COREKS_B_LY_07_U_123456";
        Assert.Throws<EncodingException>(() => DeckfmtCodec.EncodeList(list));
    }

    [Fact]
    public void EncodeList_ThrowsOnFamilyOutOfRange()
    {
        // CoreKS' numberInFactionBits is 6 (max 63) -- see DeckfmtConfigTests.
        var list = "1 ALT_COREKS_B_LY_64_R1";
        Assert.Throws<EncodingException>(() => DeckfmtCodec.EncodeList(list));
    }

    [Fact]
    public void EncodeList_FiltersOutZeroQuantity()
    {
        var list = "1 ALT_CORE_B_YZ_02_C\n0 ALT_CORE_B_AX_11_R1\n3 ALT_CORE_B_LY_28_C\n";
        var encoded = DeckfmtCodec.EncodeList(list);
        var decoded = DeckfmtCodec.DecodeList(encoded);
        Assert.Contains("1 ALT_CORE_B_YZ_02_C", decoded);
        Assert.Contains("3 ALT_CORE_B_LY_28_C", decoded);
        Assert.DoesNotContain("ALT_CORE_B_AX_11_R1", decoded);
    }

    [Fact]
    public void EncodeList_EncodesEmptyList()
    {
        var encoded = DeckfmtCodec.EncodeList("");
        Assert.Equal("IAA", encoded);
        var decoded = DeckfmtCodec.DecodeList(encoded);
        Assert.Equal("", decoded);
    }

    [Fact]
    public void CardRefElements_ParsesCardIdCorrectly()
    {
        var card = new CardRefElements("ALT_CORE_B_BR_03_C");
        Assert.Equal(2, card.SetCode); // Core
        Assert.Equal(2, card.Faction); // Bravos
        Assert.Equal(0, card.Rarity); // Common
        Assert.Equal(3, card.NumInFaction);
        Assert.Null(card.UniqNum);
    }

    [Fact]
    public void CardRefElements_ParsesUniqueCardIdCorrectly()
    {
        var card = new CardRefElements("ALT_CORE_B_LY_07_U_12345");
        Assert.Equal(2, card.SetCode); // Core
        Assert.Equal(3, card.Faction); // Lyra
        Assert.Equal(3, card.Rarity); // Unique
        Assert.Equal(7, card.NumInFaction);
        Assert.Equal(12345, card.UniqNum);
    }

    [Fact]
    public void CardRefElements_ThrowsOnMissingUniqueNumber()
    {
        Assert.Throws<ArgumentException>(() => new CardRefElements("ALT_CORE_B_LY_07_U"));
    }

    [Fact]
    public void EncodableCard_RoundTrips()
    {
        var card = EncodableCard.FromId("ALT_CORE_B_BR_03_C");
        Assert.Equal("ALT_CORE_B_BR_03_C", card.AsCardId());
    }

    [Fact]
    public void EncodableCard_RoundTripsUnique()
    {
        var card = EncodableCard.FromId("ALT_CORE_B_LY_07_U_12345");
        Assert.Equal("ALT_CORE_B_LY_07_U_12345", card.AsCardId());
    }

    [Fact]
    public void EncodableCard_RoundTripsPromo()
    {
        var card = EncodableCard.FromId("ALT_ALIZE_P_AX_01_C");
        Assert.Equal("ALT_ALIZE_P_AX_01_C", card.AsCardId());
    }
}
