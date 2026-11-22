using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Formats;
using DeckApi.Validation;

namespace DeckApi.Tests.Validation;

public class DeckValidatorTests
{
    private static readonly FormatCatalog Formats = FormatCatalog.Load(null);
    private static readonly CardCatalog Catalog = TestCards.LoadedCatalog();

    private static ValidationResult Validate(string format, List<DeckLine> lines, IReadOnlyDictionary<string, int>? pool = null)
    {
        var resolved = Catalog.ResolveAsync(lines.Select(l => l.Reference), CancellationToken.None).GetAwaiter().GetResult();
        return DeckValidator.Validate(Formats.Find(format)!, lines, resolved, pool);
    }

    private static List<string> Codes(ValidationResult r) => r.Errors.Select(e => e.Code).ToList();

    [Fact]
    public void Legal_standard_deck()
    {
        var result = Validate("standard", TestCards.StandardAxDeck());
        Assert.Empty(result.Errors);
        Assert.True(result.Legal);
        Assert.Equal(["hero", "deckSize", "faction", "sets", "bannedCards", "suspendedCards", "cards", "copies", "uniqueQuantity", "rareQuantity", "exaltedQuantity"],
            result.Detail.Select(d => d.Rule));
    }

    [Fact]
    public void Copies_are_counted_per_family_across_rarities_sets_and_prints()
    {
        var deck = TestCards.StandardAxDeck();
        deck.Add(new("ALT_CORE_A_AX_04_R1", 1)); // alt art of the same family
        deck.Add(new("ALT_ALIZE_B_AX_04_C", 1)); // reprint
        var result = Validate("standard", deck);
        var error = Assert.Single(result.Errors, e => e.Code == ValidationCodes.FamilyCopies);
        Assert.Equal("AX_04", error.Params!["family"]);
        Assert.Equal("5", error.Params["count"]);
        Assert.False(result.Detail.Single(d => d.Rule == "copies").Ok);
    }

    [Fact]
    public void Hero_and_size_errors()
    {
        var deck = TestCards.StandardAxDeck().Where(l => l.Reference != TestCards.AxHero).Take(5).ToList();
        Assert.Equal([ValidationCodes.HeroCount, ValidationCodes.DeckSize], Codes(Validate("standard", deck)));
    }

    [Fact]
    public void Two_heroes_are_reported()
    {
        var deck = TestCards.StandardAxDeck();
        deck.Add(new("ALT_CORE_P_AX_01_C", 1));
        Assert.Contains(ValidationCodes.HeroCount, Codes(Validate("standard", deck)));
    }

    [Fact]
    public void Out_of_faction_unique_uses_its_real_faction()
    {
        var deck = TestCards.StandardAxDeck();
        deck[1] = deck[1] with { Quantity = 2 };
        deck.Add(new("ALT_CORE_B_AX_04_U_661", 1)); // printed AX_04 but MU per range table
        var result = Validate("standard", deck);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.FactionsMultiple, error.Code);
        Assert.Equal("AX, MU", error.Params!["factions"]);
    }

    [Fact]
    public void Unknown_unique_serial_is_reported()
    {
        var deck = TestCards.StandardAxDeck();
        deck[1] = deck[1] with { Quantity = 2 };
        deck.Add(new("ALT_CORE_B_AX_04_U_99999", 1));
        Assert.Equal([ValidationCodes.CardUnknown], Codes(Validate("standard", deck)));
    }

    [Fact]
    public void Suspended_by_family_and_rarity_and_forbidden_set()
    {
        var deck = TestCards.StandardAxDeck();
        deck[1] = deck[1] with { Quantity = 1 };
        deck.Add(new("ALT_FUGUE_B_AX_140_C", 1));
        deck.Add(new("ALT_CORE_B_BR_06_C", 1));
        var codes = Codes(Validate("standard", deck));
        Assert.Contains(ValidationCodes.SetForbidden, codes);
        Assert.Contains(ValidationCodes.CardSuspended, codes);

        // Sandbox allows suspended cards and has no faction rule.
        Assert.DoesNotContain(ValidationCodes.CardSuspended, Codes(Validate("sandbox", deck)));
    }

    [Fact]
    public void Set_legality_is_per_gameplay_identity()
    {
        // AX_05_C is also printed in FUGUE (forbidden in standard): still legal through its CORE print,
        // whatever print the deck names. AX_140 only exists in FUGUE.
        var deck = TestCards.StandardAxDeck();
        deck[2] = new DeckLine("ALT_FUGUE_B_AX_05_C", 3);
        Assert.Empty(Validate("standard", deck).Errors);

        deck[1] = deck[1] with { Quantity = 2 };
        deck.Add(new("ALT_FUGUE_B_AX_140_C", 1));
        var error = Assert.Single(Validate("standard", deck).Errors);
        Assert.Equal((ValidationCodes.SetForbidden, "FUGUE"), (error.Code, error.Params!["set"]));
    }

    [Fact]
    public void Nuc_forbids_uniques_and_limits_rares()
    {
        var deck = TestCards.StandardAxDeck();
        deck[1] = deck[1] with { Quantity = 2 };
        deck.Add(new("ALT_CORE_B_AX_04_U_1", 1));
        for (var n = 17; n <= 22; n++) deck.Add(new($"ALT_CORE_B_AX_{n:00}_R1", 3)); // 18 rares
        var codes = Codes(Validate("nuc", deck));
        Assert.Contains(ValidationCodes.UniquesNotAllowed, codes);
        Assert.Contains(ValidationCodes.RaresTooMany, codes);
    }

    [Fact]
    public void Frontier_allowlist()
    {
        var deck = TestCards.StandardAxDeck();
        deck[1] = deck[1] with { Quantity = 2 };
        deck.Add(new("ALT_CORE_B_AX_04_U_1", 1));
        Assert.Contains(ValidationCodes.UniqueNotInAllowlist, Codes(Validate("frontier", deck)));
        Assert.DoesNotContain(ValidationCodes.UniqueNotInAllowlist, Codes(Validate("standard", deck)));
    }

    [Fact]
    public void Singleton_max_one_per_family_and_rarity()
    {
        var deck = new List<DeckLine> { new(TestCards.AxHero, 1) };
        foreach (var n in Enumerable.Range(4, 21).Where(n => n != 17)) // AX_17_R1 is suspended
        {
            deck.Add(new($"ALT_CORE_B_AX_{n:00}_C", 1));
            deck.Add(new($"ALT_CORE_B_AX_{n:00}_R1", 1));
            deck.Add(new($"ALT_CORE_B_AX_{n:00}_R2", 1));
        }
        Assert.Empty(Validate("singleton", deck).Errors.Select(e => e.Code + " " + e.Reference));

        deck.Add(new("ALT_CORE_A_AX_04_R1", 1)); // second R1 of AX_04 + 4th copy of the family
        var codes = Codes(Validate("singleton", deck));
        Assert.Contains(ValidationCodes.FamilyRarityCopies, codes);
        Assert.Contains(ValidationCodes.FamilyCopies, codes);
    }

    [Fact]
    public void Sealed_pool_membership_and_fail_closed()
    {
        var deck = TestCards.StandardAxDeck().Take(11).ToList(); // hero + 30 cards
        var pool = deck.ToDictionary(l => l.Reference, l => l.Quantity);
        Assert.True(Validate("sealed", deck, pool).Legal);

        pool[deck[1].Reference] = 1;
        pool.Remove(deck[2].Reference);
        Assert.Equal([ValidationCodes.SealedPoolExceeded, ValidationCodes.SealedNotInPool], Codes(Validate("sealed", deck, pool)));

        Assert.Equal([ValidationCodes.SealedPoolUnavailable], Codes(Validate("sealed", deck, pool: null)));
    }

    [Fact]
    public void Sealed_pool_of_reprints_matches_canonical_deck_lines()
    {
        // altered-draft lists the opened prints; the stored deck holds the canonical one.
        var rawPool = new Dictionary<string, int> { ["ALT_ALIZE_B_AX_06_C"] = 2, ["ALT_CORE_B_AX_06_C"] = 1 };
        var pool = DeckValidationService.CanonicalPool(rawPool, Catalog)!;
        Assert.Equal(3, pool["ALT_CORE_B_AX_06_C"]);
        Assert.False(pool.ContainsKey("ALT_ALIZE_B_AX_06_C"));
        Assert.Null(DeckValidationService.CanonicalPool(null, Catalog));
    }
}

public class ValidationMessagesTests
{
    private static readonly ValidationMessages Messages = ValidationMessages.LoadEmbedded();

    [Fact]
    public void All_languages_cover_every_english_key()
    {
        Assert.Equal(["de", "en", "es", "fr", "it"], Messages.Languages.Order());
        var error = new ValidationError(ValidationCodes.DeckSize, null, new() { ["min"] = "39", ["max"] = "59", ["count"] = "12" });
        foreach (var lang in Messages.Languages)
            Assert.DoesNotContain("{", Messages.Translate(error, lang).Message);
    }

    [Theory]
    [InlineData(null, "Card \"Card AX 4\" is suspended.")]
    [InlineData("pt", "Card \"Card AX 4\" is suspended.")]
    [InlineData("fr-FR", "La carte « Card AX 4 (fr) » est suspendue.")]
    public void Translates_with_card_name_in_the_same_language(string? locale, string expected)
    {
        var catalog = TestCards.LoadedCatalog();
        var errors = Messages.Translate([new ValidationError(ValidationCodes.CardSuspended, "ALT_CORE_B_AX_04_C")], locale, catalog);
        Assert.Equal(expected, Assert.Single(errors).Message);
    }
}
