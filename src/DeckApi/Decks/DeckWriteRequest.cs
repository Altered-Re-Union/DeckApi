using System.Text.Json;
using System.Text.Json.Nodes;
using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Deckfmt;
using DeckApi.Formats;

namespace DeckApi.Decks;

/// <summary>
/// Write body, old API <c>deck:write</c> group plus two optional additions:
/// <c>altArtOverrides</c> and <c>compressed</c> (a Deckfmt string accepted instead of <c>deckCards</c>).
/// Parsed by hand to honour JSON merge-patch semantics on PATCH (absent ≠ null).
/// </summary>
public sealed class DeckWriteRequest
{
    public Optional<string> Name { get; private set; }
    public Optional<string?> Description { get; private set; }
    public Optional<FormatDefinition?> Format { get; private set; }
    public Optional<bool> IsPublic { get; private set; }
    public Optional<bool> IsDraft { get; private set; }
    public Optional<List<DeckLine>> DeckCards { get; private set; }
    public Optional<string?> AlteredId { get; private set; }
    public Optional<AltArtOverrides?> AltArtOverrides { get; private set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DeckWriteRequest Parse(JsonObject body, bool isCreate, IFormatCatalog formats, List<InputViolation> violations)
    {
        var request = new DeckWriteRequest();

        if (body.TryGetPropertyValue("name", out var name))
        {
            var value = AsString(name);
            if (string.IsNullOrWhiteSpace(value)) violations.Add(new("name", "This value should not be blank."));
            else if (value.Length > 150) violations.Add(new("name", "This value is too long. It should have 150 characters or less."));
            else request.Name = new(value);
        }
        else if (isCreate)
        {
            violations.Add(new("name", "This value should not be blank."));
        }

        if (body.TryGetPropertyValue("description", out var description))
        {
            var value = AsString(description);
            if (value is { Length: > 5000 }) violations.Add(new("description", "This value is too long. It should have 5000 characters or less."));
            else request.Description = new(value);
        }

        if (body.TryGetPropertyValue("format", out var formatNode))
        {
            var code = AsString(formatNode);
            if (string.IsNullOrWhiteSpace(code)) request.Format = new(null);
            else if (formats.Find(code) is { } format) request.Format = new(format);
            else violations.Add(new("format", $"\"{code}\" is not a valid format."));
        }

        if (body.TryGetPropertyValue("isPublic", out var isPublic)) request.IsPublic = new(AsBool(isPublic));
        if (body.TryGetPropertyValue("isDraft", out var isDraft)) request.IsDraft = new(AsBool(isDraft));

        if (body.TryGetPropertyValue("alteredId", out var alteredId))
        {
            var value = AsString(alteredId);
            request.AlteredId = new(string.IsNullOrEmpty(value) ? null : value);
        }

        if (body.TryGetPropertyValue("deckCards", out var deckCards) && deckCards is not null)
        {
            request.DeckCards = new(ParseDeckCards(deckCards, violations));
        }
        else if (body.TryGetPropertyValue("compressed", out var compressed) && AsString(compressed) is { Length: > 0 } encoded)
        {
            try
            {
                request.DeckCards = new(DeckContent.Decode(encoded));
            }
            catch (Exception e) when (e is FormatException or DecodingException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                violations.Add(new("compressed", "Invalid compressed deck."));
            }
        }

        if (body.TryGetPropertyValue("altArtOverrides", out var overrides))
        {
            try
            {
                request.AltArtOverrides = new(overrides?.Deserialize<AltArtOverrides>(Json));
            }
            catch (JsonException)
            {
                violations.Add(new("altArtOverrides", "Expected {\"cards\": {\"<reference>\": [{\"reference\", \"quantity\"}]}, \"tokens\": [{\"reference\", \"quantity\"}]}."));
            }
        }

        return request;
    }

    private static List<DeckLine> ParseDeckCards(JsonNode node, List<InputViolation> violations)
    {
        var lines = new List<DeckLine>();
        if (node is not JsonArray array)
        {
            violations.Add(new("deckCards", "This value should be of type array."));
            return lines;
        }
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item) continue;
            var reference = AsString(item["cardReference"]);
            var quantity = item.TryGetPropertyValue("quantity", out var q) && q is not null ? AsInt(q) : 1;
            if (string.IsNullOrWhiteSpace(reference))
                violations.Add(new($"deckCards[{i}].cardReference", "This value should not be blank."));
            else if (!CardReference.IsWellFormed(reference))
                violations.Add(new($"deckCards[{i}].cardReference", "Invalid card reference format. Expected format: ALT_CORE_B_AX_01_C"));
            if (quantity is null or < 1 or > 100)
                violations.Add(new($"deckCards[{i}].quantity", "This value should be between 1 and 100."));
            if (reference is not null && quantity is { } qty) lines.Add(new DeckLine(reference, qty));
        }
        return lines;
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToString();

    private static bool AsBool(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) && p);

    private static int? AsInt(JsonNode node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return null;
    }
}

public readonly record struct Optional<T>(T Value)
{
    public bool HasValue { get; } = true;
}
