using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Formats;

namespace DeckApi.Validation;

public sealed record ValidationResult(List<ValidationError> Errors, List<LegalityRule> Detail)
{
    public bool Legal => Detail.All(r => r.Ok);
}

/// <summary>
/// Format-agnostic rules engine driven by <see cref="FormatDefinition"/>. Differences with the old PHP
/// validators, on purpose:
/// <list type="bullet">
/// <item>"same card" is the family <c>{FAC}_{NUM}</c> (was the localized card name);</item>
/// <item>unknown references are reported (<c>card.unknown</c>) instead of silently counted as commons;</item>
/// <item>several heroes are reported instead of the last one silently winning;</item>
/// <item>a Unique's faction is its real faction (range tables), not its reference prefix;</item>
/// <item>set legality is per gameplay identity: legal when printed in at least one allowed set.</item>
/// </list>
/// Error order is kept: cards, sets, hero, size, faction, banned/suspended, format rules.
/// </summary>
public static class DeckValidator
{
    private static readonly string[] BaseRules = ["hero", "deckSize", "faction", "sets", "bannedCards", "suspendedCards", "cards"];

    /// <param name="sealedPool">Sealed only: reference → copies in the player's pool; null = pool unavailable (fails closed).</param>
    public static ValidationResult Validate(
        FormatDefinition format,
        IReadOnlyList<DeckLine> lines,
        IReadOnlyDictionary<string, ResolvedCard> resolved,
        IReadOnlyDictionary<string, int>? sealedPool = null)
    {
        var errors = new List<ValidationError>();
        var cards = lines
            .Where(l => l.Quantity > 0)
            .Select(l => (Line: l, Card: resolved.GetValueOrDefault(l.Reference) ?? new ResolvedCard(null, false, false, null, null)))
            .ToList();

        // Unknown cards.
        foreach (var (line, card) in cards.Where(c => !c.Card.Known))
            errors.Add(new ValidationError(ValidationCodes.CardUnknown, line.Reference));

        // Sets (hero included): the card's gameplay identity (family + rarity) must be printed in at least
        // one allowed set — the print/set stored in the deck doesn't matter (a reprint or an alt art of a
        // legal card is legal). A Unique only exists in its own set.
        foreach (var (line, card) in cards)
        {
            if (card.Reference is null || !card.Known) continue;
            var printedIn = card.PrintedIn;
            var anyForbidden = false;
            if (printedIn.Any(set => format.IsSetAllowed(set, out _))) continue;
            foreach (var set in printedIn) { format.IsSetAllowed(set, out var f); anyForbidden |= f; }
            errors.Add(new ValidationError(anyForbidden ? ValidationCodes.SetForbidden : ValidationCodes.SetNotAllowed,
                line.Reference, new() { ["set"] = string.Join(", ", printedIn) }));
        }

        // Hero.
        var heroes = cards.Where(c => c.Card.IsHero).ToList();
        var deck = cards.Where(c => !c.Card.IsHero).ToList();
        var hero = heroes.Count == 1 && heroes[0].Line.Quantity == 1 ? heroes[0] : default;
        if (format.RequireHero && hero.Line is null)
            errors.Add(new ValidationError(ValidationCodes.HeroCount, null, new() { ["count"] = heroes.Sum(h => h.Line.Quantity).ToString() }));

        // Size (hero excluded).
        var size = deck.Sum(c => c.Line.Quantity);
        if (size < format.MinCards || (format.MaxCards is { } max && size > max))
        {
            errors.Add(format.MaxCards is { } m
                ? new ValidationError(ValidationCodes.DeckSize, null, new() { ["min"] = format.MinCards.ToString(), ["max"] = m.ToString(), ["count"] = size.ToString() })
                : new ValidationError(ValidationCodes.DeckSizeMin, null, new() { ["min"] = format.MinCards.ToString(), ["count"] = size.ToString() }));
        }

        // Factions (NE ignored; cards first, then hero, first-seen order).
        if (format.MaxFactions is { } maxFactions)
        {
            var factions = deck.Concat(heroes)
                .Select(c => c.Card.Faction)
                .Where(f => f is not null && f != "NE")
                .Distinct()
                .ToList();
            if (factions.Count > maxFactions)
            {
                var list = string.Join(", ", factions);
                errors.Add(maxFactions == 1
                    ? new ValidationError(ValidationCodes.FactionsMultiple, null, new() { ["factions"] = list })
                    : new ValidationError(ValidationCodes.FactionsTooMany, null, new() { ["count"] = factions.Count.ToString(), ["max"] = maxFactions.ToString(), ["factions"] = list }));
            }
        }

        // Banned / suspended (hero included).
        foreach (var (line, card) in cards)
        {
            if (card.Reference is null) continue;
            if (!format.AllowBanned && format.BannedMatcher.Matches(card.Reference))
                errors.Add(new ValidationError(ValidationCodes.CardBanned, line.Reference));
            if (!format.AllowSuspended && format.SuspendedMatcher.Matches(card.Reference))
                errors.Add(new ValidationError(ValidationCodes.CardSuspended, line.Reference));
        }

        var formatRules = new List<string>();

        // Copies.
        var hasCopiesRule = format.UniqueSingleCopy || format.MaxCopiesPerFamily is not null || format.MaxCopiesPerRarity is not null;
        if (hasCopiesRule) formatRules.Add("copies");
        var parsedDeck = deck.Where(c => c.Card.Reference is not null).ToList();

        if (format.UniqueSingleCopy)
        {
            foreach (var (line, _) in parsedDeck.Where(c => c.Card.Reference!.IsUnique && c.Line.Quantity > 1))
                errors.Add(new ValidationError(ValidationCodes.UniqueCopies, line.Reference, new() { ["count"] = line.Quantity.ToString() }));
        }

        if (format.MaxCopiesPerFamily is { } maxPerFamily)
        {
            foreach (var family in parsedDeck.GroupBy(c => c.Card.Reference!.Family))
            {
                var count = family.Sum(c => c.Line.Quantity);
                if (count > maxPerFamily)
                    errors.Add(new ValidationError(ValidationCodes.FamilyCopies, family.First().Line.Reference,
                        new() { ["family"] = family.Key, ["max"] = maxPerFamily.ToString(), ["count"] = count.ToString() }));
            }
        }

        if (format.MaxCopiesPerRarity is { } maxPerRarity)
        {
            foreach (var group in parsedDeck.GroupBy(c => (c.Card.Reference!.Family, c.Card.Reference!.RarityCode)))
            {
                var count = group.Sum(c => c.Line.Quantity);
                if (count > maxPerRarity)
                    errors.Add(new ValidationError(ValidationCodes.FamilyRarityCopies, group.First().Line.Reference,
                        new() { ["family"] = group.Key.Family, ["rarity"] = group.Key.RarityCode, ["max"] = maxPerRarity.ToString(), ["count"] = count.ToString() }));
            }
        }

        // Rarity quotas.
        int CountRarity(Rarity r) => parsedDeck.Where(c => c.Card.Reference!.Rarity == r).Sum(c => c.Line.Quantity);
        int CountRares() => parsedDeck.Where(c => c.Card.Reference!.Rarity is Rarity.Rare1 or Rarity.Rare2).Sum(c => c.Line.Quantity);

        if (format.MaxUniques is not null || format.UniqueLimitsByHero is not null)
        {
            formatRules.Add("uniqueQuantity");
            var uniques = CountRarity(Rarity.Unique);
            int? limit = format.UniqueLimitsByHero is null ? format.MaxUniques
                : hero.Line is null ? 0
                : format.UniqueLimitForHero(hero.Card.EnglishName);
            if (limit is { } l && uniques > l)
            {
                if (l == 0 && format.UniqueLimitsByHero is null)
                    errors.Add(new ValidationError(ValidationCodes.UniquesNotAllowed, null, new() { ["count"] = uniques.ToString() }));
                else if (format.UniqueLimitsByHero is not null && hero.Line is not null)
                    errors.Add(new ValidationError(ValidationCodes.UniquesHeroLimit, hero.Line.Reference, new() { ["max"] = l.ToString(), ["count"] = uniques.ToString() }));
                else
                    errors.Add(new ValidationError(ValidationCodes.UniquesTooMany, null, new() { ["max"] = l.ToString(), ["count"] = uniques.ToString() }));
            }
        }

        if (hasCopiesRule || format.MaxRares is not null)
        {
            formatRules.Add("rareQuantity");
            if (format.MaxRares is { } maxRares && CountRares() is var rares && rares > maxRares)
                errors.Add(new ValidationError(ValidationCodes.RaresTooMany, null, new() { ["max"] = maxRares.ToString(), ["count"] = rares.ToString() }));
        }

        if (hasCopiesRule || format.MaxExalted is not null)
        {
            formatRules.Add("exaltedQuantity");
            if (format.MaxExalted is { } maxExalted && CountRarity(Rarity.Exalted) is var exalted && exalted > maxExalted)
                errors.Add(new ValidationError(ValidationCodes.ExaltedTooMany, null, new() { ["max"] = maxExalted.ToString(), ["count"] = exalted.ToString() }));
        }

        // Unique allowlist (frontier).
        if (format.UniqueAllowlistRefs is { } allowlist)
        {
            formatRules.Add("frontierUniques");
            foreach (var (line, card) in parsedDeck.Where(c => c.Card.Reference!.IsUnique))
            {
                if (!allowlist.Contains(line.Reference) && !allowlist.Contains(card.Reference!.BoosterReference))
                    errors.Add(new ValidationError(ValidationCodes.UniqueNotInAllowlist, line.Reference, new() { ["format"] = format.Label }));
            }
        }

        // Sealed: every card, hero included, must be in the player's own pool.
        if (format.Special == "sealed")
        {
            formatRules.Add("pool");
            if (sealedPool is null)
            {
                errors.Add(new ValidationError(ValidationCodes.SealedPoolUnavailable));
            }
            else
            {
                foreach (var (line, _) in cards)
                {
                    var available = sealedPool.GetValueOrDefault(line.Reference);
                    if (available == 0)
                        errors.Add(new ValidationError(ValidationCodes.SealedNotInPool, line.Reference));
                    else if (line.Quantity > available)
                        errors.Add(new ValidationError(ValidationCodes.SealedPoolExceeded, line.Reference,
                            new() { ["pool"] = available.ToString(), ["count"] = line.Quantity.ToString() }));
                }
            }
        }

        var failed = errors.Select(e => e.Rule).ToHashSet();
        var detail = BaseRules.Concat(formatRules)
            .Select(rule => new LegalityRule(rule, !failed.Contains(rule)))
            .ToList();
        return new ValidationResult(errors, detail);
    }
}
