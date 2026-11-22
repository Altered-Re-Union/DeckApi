namespace DeckApi.Validation;

/// <summary>
/// One deck-legality problem, stored untranslated. <see cref="Code"/> is stable (clients may switch
/// on it), <see cref="Reference"/> is the card the error is about (null for deck-level errors),
/// <see cref="Params"/> fills the message template. A param whose key ends with <c>Ref</c> holds a card
/// reference and is rendered as that card's localized name.
/// </summary>
public sealed record ValidationError(string Code, string? Reference = null, Dictionary<string, string>? Params = null)
{
    /// <summary>Legality-detail rule this error falsifies (old API <c>legalityDetail</c> keys).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Rule => ValidationCodes.RuleOf(Code);
}

public static class ValidationCodes
{
    public const string CardUnknown = "card.unknown";
    public const string SetForbidden = "card.set.forbidden";
    public const string SetNotAllowed = "card.set.notAllowed";
    public const string HeroCount = "deck.hero.count";
    public const string DeckSize = "deck.size";
    public const string DeckSizeMin = "deck.size.min";
    public const string FactionsMultiple = "deck.factions.multiple";
    public const string FactionsTooMany = "deck.factions.tooMany";
    public const string CardBanned = "card.banned";
    public const string CardSuspended = "card.suspended";
    public const string UniqueCopies = "card.unique.copies";
    public const string FamilyCopies = "card.family.copies";
    public const string FamilyRarityCopies = "card.family.rarityCopies";
    public const string UniquesNotAllowed = "deck.uniques.notAllowed";
    public const string UniquesTooMany = "deck.uniques.tooMany";
    public const string UniquesHeroLimit = "deck.uniques.heroLimit";
    public const string RaresTooMany = "deck.rares.tooMany";
    public const string ExaltedTooMany = "deck.exalted.tooMany";
    public const string UniqueNotInAllowlist = "card.unique.notInAllowlist";
    public const string SealedPoolUnavailable = "sealed.pool.unavailable";
    public const string SealedNotInPool = "sealed.pool.missing";
    public const string SealedPoolExceeded = "sealed.pool.exceeded";

    public static string RuleOf(string code) => code switch
    {
        CardUnknown => "cards",
        SetForbidden or SetNotAllowed => "sets",
        HeroCount => "hero",
        DeckSize or DeckSizeMin => "deckSize",
        FactionsMultiple or FactionsTooMany => "faction",
        CardBanned => "bannedCards",
        CardSuspended => "suspendedCards",
        UniqueCopies or FamilyCopies or FamilyRarityCopies => "copies",
        UniquesNotAllowed or UniquesTooMany or UniquesHeroLimit => "uniqueQuantity",
        RaresTooMany => "rareQuantity",
        ExaltedTooMany => "exaltedQuantity",
        UniqueNotInAllowlist => "frontierUniques",
        SealedPoolUnavailable or SealedNotInPool or SealedPoolExceeded => "pool",
        _ => "other",
    };
}
