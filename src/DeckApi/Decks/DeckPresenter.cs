using System.Text.Json.Nodes;
using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Validation;

namespace DeckApi.Decks;

/// <summary>
/// Renders decks in the old API's JSON shape (same keys, same order, same nulls) plus additive fields
/// that old clients simply ignore:
/// <list type="bullet">
/// <item><c>errors</c>: <c>[{code, reference, message}]</c>, translated with <c>?locale=</c> (English default);</item>
/// <item><c>compressed</c>: the Deckfmt-encoded list (hero included);</item>
/// <item><c>altArtOverrides</c>: per-deck print choices keyed by canonical reference, or null;</item>
/// <item><c>canonicalCards</c> (item GET): the stored list, one canonical reference per gameplay card.
/// The old <c>cards</c>/<c>deckCards</c> fields show the concrete prints (overrides applied), like the old API;</item>
/// <item><c>owner</c>: <c>{pseudo}</c> on public listings and public deck pages;</item>
/// <item><c>legacyDeletedAt</c>: when the deck went missing from the old API (it was then archived, <c>isDraft: true</c>).</item>
/// </list>
/// </summary>
public sealed class DeckPresenter(ICardCatalog catalog, ValidationMessages messages, IConfiguration config)
{
    /// <summary>Default card-name locale: French, like the old API.</summary>
    public const string DefaultCardLocale = "fr";

    public JsonObject Render(Deck deck, string? locale, bool includeOwner = false, bool? hasUpvoted = null)
    {
        var cardLocale = locale ?? DefaultCardLocale;
        var errors = messages.Translate(deck.ValidationErrors, locale, catalog);

        var json = new JsonObject
        {
            ["id"] = deck.Id.ToString(),
            ["name"] = deck.Name,
            ["description"] = deck.Description,
            ["format"] = deck.Format,
            ["isPublic"] = deck.IsPublic,
            ["isDraft"] = deck.IsDraft,
            ["user"] = new JsonArray(),
            ["createdAt"] = Date(deck.CreatedAt),
            ["updatedAt"] = deck.UpdatedAt is { } u ? Date(u) : null,
            ["stats"] = Stats(deck, cardLocale),
            ["formatErrors"] = errors.Count > 0 ? new JsonArray(errors.Select(e => (JsonNode)e.Message).ToArray()) : null,
            ["legal"] = deck.Legal,
            ["legalityDetail"] = LegalityDetail(deck.LegalityDetail),
            ["alteredId"] = deck.AlteredId,
            ["viewCount"] = deck.ViewCount,
            ["upvoteCount"] = deck.UpvoteCount,
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode)new JsonObject
            {
                ["code"] = e.Code,
                ["reference"] = e.Reference,
                ["message"] = e.Message,
            }).ToArray()),
            ["compressed"] = deck.Compressed,
            ["altArtOverrides"] = deck.AltArtOverrides is { } o ? JsonSerializerHelpers.ToNode(o) : null,
            ["source"] = deck.Source == DeckSource.Tournament ? "tournament" : "user",
            ["tournaments"] = new JsonArray(deck.TournamentResults
                .OrderByDescending(r => r.LastPlayedAt)
                .Select(r => (JsonNode)new JsonObject
                {
                    ["tournamentParentId"] = r.TournamentParentId,
                    ["name"] = r.TournamentName,
                    ["mode"] = r.Mode,
                    ["games"] = r.Games,
                    ["wins"] = r.Wins,
                    ["losses"] = r.Losses,
                    ["lastPlayedAt"] = r.LastPlayedAt is { } at ? Date(at) : null,
                }).ToArray()),
            ["validSeasons"] = new JsonArray(deck.ValidSeasons.Select(key =>
            {
                var (format, season) = key.Split(':', 2) is [var f, var s] ? (f, s) : (key, key);
                return (JsonNode)new JsonObject { ["format"] = format, ["season"] = season };
            }).ToArray()),
            ["legacyDeletedAt"] = deck.LegacyDeletedAt is { } missing ? Date(missing) : null,
        };
        if (deck.ExtraCards is { Count: > 0 })
            json["compressedExtraCards"] = JsonSerializerHelpers.ToNode(deck.ExtraCards);
        if (includeOwner)
            json["owner"] = new JsonObject { ["pseudo"] = deck.User?.Username };
        if (hasUpvoted is { } h)
            json["hasUpvoted"] = h;
        return json;
    }

    /// <summary>Item GET: adds <c>cards</c> (old API card detail shape) or <c>deckCards: []</c> when empty.</summary>
    public async Task<JsonObject> RenderDetailAsync(Deck deck, string? locale, bool includeOwner, CancellationToken ct)
    {
        var json = Render(deck, locale, includeOwner);
        var canonicalLines = DeckContent.Read(deck);
        json["canonicalCards"] = new JsonArray(canonicalLines.Select(l => (JsonNode)new JsonObject
        {
            ["cardReference"] = l.Reference,
            ["quantity"] = l.Quantity,
        }).ToArray());
        var lines = DeckCanonicalizer.Materialize(canonicalLines, deck.AltArtOverrides);
        if (lines.Count == 0)
        {
            json["deckCards"] = new JsonArray();
            return json;
        }

        var cardLocale = locale ?? DefaultCardLocale;
        var cards = await catalog.GetCardsAsync(lines.Select(l => l.Reference), ct);
        json["cards"] = new JsonArray(lines.Select(line =>
        {
            cards.TryGetValue(line.Reference, out var card);
            return (JsonNode)new JsonObject
            {
                ["cardReference"] = line.Reference,
                ["quantity"] = line.Quantity,
                ["name"] = card is null ? null : CatalogCard.Localize(card.Name, cardLocale),
                ["factionCode"] = card?.Faction?.Code,
                ["cardTypeReference"] = card?.CardType?.Reference,
                ["mainCost"] = card?.MainCost,
                ["recallCost"] = card?.RecallCost,
                ["oceanPower"] = card?.OceanPower,
                ["mountainPower"] = card?.MountainPower,
                ["forestPower"] = card?.ForestPower,
                ["imagePath"] = ImagePath(line.Reference, cardLocale),
                ["effects"] = Effects(card, cardLocale),
            };
        }).ToArray());
        return json;
    }

    /// <summary>
    /// uniques-search-api only has the full main/echo effect texts (no trigger/condition/effect split):
    /// one entry per non-empty text, ability parts null.
    /// </summary>
    private static JsonArray Effects(CatalogCard? card, string locale)
    {
        var result = new JsonArray();
        if (card is null) return result;
        foreach (var text in new[] { CatalogCard.Localize(card.MainEffect, locale), CatalogCard.Localize(card.EchoEffect, locale) })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            result.Add(new JsonObject
            {
                ["text"] = text,
                ["abilityTrigger"] = null,
                ["abilityCondition"] = null,
                ["abilityEffect"] = null,
            });
        }
        return result;
    }

    public JsonObject Stats(Deck deck, string locale)
    {
        JsonNode? hero = null;
        if (deck.HeroReference is { } canonicalHero)
        {
            var heroRef = DeckCanonicalizer.DisplayReference(canonicalHero, deck.AltArtOverrides);
            hero = new JsonObject
            {
                ["reference"] = heroRef,
                ["name"] = ValidationMessages.CardName(catalog, heroRef, locale),
                ["imagePath"] = ImagePath(heroRef, locale),
            };
        }
        return new JsonObject
        {
            ["totalCards"] = deck.CardCount,
            ["hero"] = hero,
            ["byRarity"] = new JsonObject
            {
                ["C"] = deck.ByRarity.C,
                ["R"] = deck.ByRarity.R,
                ["U"] = deck.ByRarity.U,
                ["E"] = deck.ByRarity.E,
            },
        };
    }

    /// <summary><c>Cards:ImageUrlTemplate</c> with <c>{reference}</c> and <c>{locale}</c> placeholders; null when unset.</summary>
    public string? ImagePath(string reference, string locale)
    {
        var template = config["Cards:ImageUrlTemplate"];
        if (string.IsNullOrEmpty(template)) return null;
        var lang = locale.Split('-', '_')[0].ToLowerInvariant();
        return template.Replace("{reference}", reference).Replace("{locale}", lang);
    }

    public static JsonObject? LegalityDetail(List<LegalityRule>? rules)
    {
        if (rules is null) return null;
        var json = new JsonObject();
        foreach (var rule in rules) json[rule.Rule] = rule.Ok;
        json["global"] = rules.All(r => r.Ok);
        return json;
    }

    /// <summary>RFC 3339 with explicit offset, like the old API (<c>2024-11-01T10:00:00+00:00</c>).</summary>
    public static string Date(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz");
}

internal static class JsonSerializerHelpers
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static JsonNode? ToNode<T>(T value) => System.Text.Json.JsonSerializer.SerializeToNode(value, Options);
}
