using DeckApi.Cards;
using DeckApi.Data;

namespace DeckApi.Decks;

/// <summary>
/// Checks for the optional <c>altArtOverrides</c> field. Overrides are purely cosmetic: they never change
/// what the deck "is" for validation, so a replacement must be a print of the same family and the
/// replaced quantities cannot exceed what the deck holds.
/// </summary>
public static class AltArtOverridesRules
{
    public static List<InputViolation> Validate(AltArtOverrides overrides, IReadOnlyList<DeckLine> lines)
    {
        var violations = new List<InputViolation>();
        var quantities = lines.ToDictionary(l => l.Reference, l => l.Quantity, StringComparer.Ordinal);

        foreach (var (baseRef, replacements) in overrides.Cards)
        {
            var path = $"altArtOverrides.cards[{baseRef}]";
            if (!quantities.TryGetValue(baseRef, out var quantity))
            {
                violations.Add(new(path, $"Card \"{baseRef}\" is not in the deck."));
                continue;
            }
            if (!CardReference.TryParse(baseRef, out var parsedBase)) continue;

            foreach (var replacement in replacements)
            {
                if (!CardReference.TryParse(replacement.Reference, out var parsed))
                    violations.Add(new(path, $"Invalid card reference \"{replacement.Reference}\"."));
                else if (parsed.Family != parsedBase.Family || parsed.Rarity != parsedBase.Rarity)
                    violations.Add(new(path, $"\"{replacement.Reference}\" is not a print of \"{baseRef}\" (same family and rarity required)."));
                if (replacement.Quantity < 1)
                    violations.Add(new(path, "Quantity must be at least 1."));
            }
            var total = replacements.Sum(r => r.Quantity);
            if (total > quantity)
                violations.Add(new(path, $"Overrides cover {total} copies but the deck has {quantity}."));
        }

        foreach (var token in overrides.Tokens)
        {
            if (!CardReference.IsWellFormed(token.Reference))
                violations.Add(new("altArtOverrides.tokens", $"Invalid card reference \"{token.Reference}\"."));
            if (token.Quantity < 1)
                violations.Add(new("altArtOverrides.tokens", "Quantity must be at least 1."));
        }
        return violations;
    }
}

public sealed record InputViolation(string PropertyPath, string Message);
