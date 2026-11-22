using System.Text.Json;
using System.Text.RegularExpressions;
using DeckApi.Cards;

namespace DeckApi.Validation;

/// <summary>Translated error as returned by the API.</summary>
public sealed record TranslatedError(string Code, string? Reference, string Message);

/// <summary>
/// Translates <see cref="ValidationError"/>s with the embedded <c>Validation/Messages/{lang}.json</c>
/// catalogs. Unknown/missing locale → English. Adding a language = adding a JSON file.
/// <c>{card}</c> is the error's card, rendered with its localized name (reference as fallback).
/// </summary>
public sealed partial class ValidationMessages
{
    public const string DefaultLanguage = "en";

    private readonly Dictionary<string, Dictionary<string, string>> _catalogs;

    private ValidationMessages(Dictionary<string, Dictionary<string, string>> catalogs) => _catalogs = catalogs;

    public IReadOnlyCollection<string> Languages => _catalogs.Keys;

    public static ValidationMessages LoadEmbedded()
    {
        var assembly = typeof(ValidationMessages).Assembly;
        var catalogs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            const string marker = ".Validation.Messages.";
            var idx = name.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var lang = name[(idx + marker.Length)..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(name)!;
            catalogs[lang] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        return new ValidationMessages(catalogs);
    }

    /// <summary>Normalizes <c>fr</c>, <c>fr-FR</c>, <c>fr_FR</c> → <c>fr</c>; unsupported → <c>en</c>.</summary>
    public string ResolveLanguage(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return DefaultLanguage;
        var lang = locale.Split('-', '_')[0].Trim().ToLowerInvariant();
        return _catalogs.ContainsKey(lang) ? lang : DefaultLanguage;
    }

    public TranslatedError Translate(ValidationError error, string? locale, Func<string, string?>? cardName = null)
    {
        var lang = ResolveLanguage(locale);
        var template = _catalogs[lang].GetValueOrDefault(error.Code)
                       ?? _catalogs[DefaultLanguage].GetValueOrDefault(error.Code)
                       ?? error.Code;

        var message = Placeholder().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (key == "card")
                return error.Reference is null ? "" : cardName?.Invoke(error.Reference) ?? error.Reference;
            return error.Params?.GetValueOrDefault(key) ?? m.Value;
        });
        return new TranslatedError(error.Code, error.Reference, message);
    }

    public List<TranslatedError> Translate(IEnumerable<ValidationError>? errors, string? locale, ICardCatalog catalog)
    {
        var lang = ResolveLanguage(locale);
        return errors?.Select(e => Translate(e, lang, reference => CardName(catalog, reference, lang))).ToList() ?? [];
    }

    /// <summary>Localized card name from the catalog, falling back to the family's name.</summary>
    public static string? CardName(ICardCatalog catalog, string reference, string? locale)
    {
        var print = catalog.GetPrint(reference);
        if (print is not null) return CatalogCard.Localize(print.Name, locale);
        return CardReference.TryParse(reference, out var r) && catalog.GetFamily(r.Family) is { } family
            ? CatalogCard.Localize(family.Name, locale)
            : null;
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
