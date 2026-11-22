using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace DeckApi.Cards;

public enum Rarity { Common, Rare1, Rare2, Unique, Exalted }

/// <summary>
/// A parsed Altered card reference: <c>ALT_{SET}_{PRODUCT}_{FACTION}_{NUM}_{RARITY}[_{SERIAL}]</c>.
/// Product is B (booster), A (alt art) or P (promo) — A and P are the same gameplay card as B.
/// </summary>
public sealed partial record CardReference(
    string Value,
    string Set,
    char Product,
    string Faction,
    int Number,
    Rarity Rarity,
    int? Serial)
{
    /// <summary>
    /// Gameplay family, <c>{FACTION}_{NUM:00}</c> (e.g. <c>AX_04</c>). Family numbers are global
    /// per faction across sets, so a reprint in another set or an alt art belongs to the same family.
    /// Same key as the altered-draft unique range tables and 1:1 with CardsData's CardFamilyId.
    /// </summary>
    public string Family => $"{Faction}_{Number:00}";

    public bool IsUnique => Rarity == Rarity.Unique;

    /// <summary>Rarity code as used by the old API and by error messages: C, R1, R2, U, E.</summary>
    public string RarityCode => Rarity switch
    {
        Rarity.Common => "C",
        Rarity.Rare1 => "R1",
        Rarity.Rare2 => "R2",
        Rarity.Unique => "U",
        Rarity.Exalted => "E",
        _ => "C",
    };

    /// <summary>Rarity bucket used by deck stats (<c>byRarity</c>): C, R, U, E.</summary>
    public string RarityBucket => Rarity switch
    {
        Rarity.Rare1 or Rarity.Rare2 => "R",
        Rarity.Unique => "U",
        Rarity.Exalted => "E",
        _ => "C",
    };

    /// <summary>Same card as booster print (product B), keeping set, rarity and serial.</summary>
    public string BoosterReference => Product == 'B' ? Value : BuildReference(Set, 'B', Faction, Number, RarityCode, Serial, Value);

    // Same leniency as the old API's DeckCard regex, then a stricter structural parse.
    [GeneratedRegex(@"^ALT_([A-Z0-9]+)_([A-Z0-9]+)_([A-Z]+)_(\d+)_([A-Z0-9]+)(?:_(\d+))?$")]
    private static partial Regex Pattern();

    /// <summary>Format accepted on write, identical to the old API's validation regex.</summary>
    public static bool IsWellFormed(string? value) => value is not null && Pattern().IsMatch(value);

    public static bool TryParse(string? value, [NotNullWhen(true)] out CardReference? reference)
    {
        reference = null;
        if (value is null) return false;
        var m = Pattern().Match(value);
        if (!m.Success) return false;

        var product = m.Groups[2].Value;
        if (product.Length != 1) return false;
        var faction = m.Groups[3].Value;
        if (faction.Length != 2) return false;
        if (!int.TryParse(m.Groups[4].Value, out var number)) return false;

        Rarity? rarity = m.Groups[5].Value switch
        {
            "C" => Rarity.Common,
            "R1" or "R" => Rarity.Rare1,
            "R2" => Rarity.Rare2,
            "U" => Rarity.Unique,
            "E" or "EX" or "EXALT" => Rarity.Exalted,
            _ => null,
        };
        if (rarity is null) return false;

        int? serial = m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : null;
        if (rarity == Rarity.Unique && serial is null) return false;

        reference = new CardReference(value, m.Groups[1].Value, product[0], faction, number, rarity.Value, serial);
        return true;
    }

    public static CardReference Parse(string value) =>
        TryParse(value, out var r) ? r : throw new FormatException($"Invalid card reference '{value}'.");

    private static string BuildReference(string set, char product, string faction, int number, string rarity, int? serial, string original)
    {
        // Preserve the original zero-padding of the number (COREKS_NE_1 quirk).
        var parts = original.Split('_');
        var num = parts.Length > 4 ? parts[4] : number.ToString("00");
        var s = $"ALT_{set}_{product}_{faction}_{num}_{rarity}";
        return serial is null ? s : $"{s}_{serial}";
    }

    public override string ToString() => Value;
}
