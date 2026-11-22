using System.Text.Json.Nodes;
using DeckApi.Auth;

namespace DeckApi.Formats;

public static class FormatEndpoints
{
    public static void MapFormatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/formats", List);
        app.MapGet("/api/me", Me).RequireAuthorization(AuthPolicies.User);
    }

    /// <summary>
    /// GET /api/formats[?hiddenFormats=true] — old API keys (<c>code</c>, <c>label</c>, <c>hidden</c>,
    /// <c>minCards</c>, <c>maxCards</c>, <c>allowBanned</c>, <c>allowSuspended</c>, <c>limits</c>,
    /// <c>uniqueLimitsByHero</c>) plus everything a front needs to show the rules without re-implementing
    /// them: <c>maxFactions</c>, <c>sets.allowed/forbidden</c>, <c>bannedCards</c>, <c>suspendedCards</c>,
    /// <c>uniqueAllowlist</c> (all of them for the current season), <c>season</c> (current season code) and
    /// <c>seasons</c> (<c>code</c>, <c>label</c>, <c>start</c>, <c>end</c>). Ban entries are references, families (<c>BR_06</c>) or family+rarity (<c>OR_27_R1</c>).
    /// </summary>
    private static IResult List(HttpContext http, IFormatCatalog formats)
    {
        var showHidden = http.Request.Query["hiddenFormats"].ToString() is "true" or "1";
        var result = new JsonArray();
        foreach (var f in formats.All.Where(f => showHidden || !f.Hidden))
        {
            var json = new JsonObject { ["code"] = f.Code, ["label"] = f.Label };
            if (f.Hidden) json["hidden"] = true;
            json["minCards"] = f.MinCards;
            json["maxCards"] = f.MaxCards;
            json["allowBanned"] = f.AllowBanned;
            json["allowSuspended"] = f.AllowSuspended;
            json["limits"] = new JsonObject
            {
                ["unique"] = f.UniqueLimitsByHero is null ? f.MaxUniques : null,
                ["rare"] = f.MaxRares,
                ["exalted"] = f.MaxExalted,
                ["maxCopiesPerName"] = f.MaxCopiesPerFamily,
                ["maxCopiesPerRarity"] = f.MaxCopiesPerRarity,
            };
            if (f.UniqueLimitsByHero is { } byHero)
            {
                var limits = new JsonObject();
                foreach (var (limit, names) in byHero) limits[limit] = new JsonArray(names.Select(n => (JsonNode)n).ToArray());
                json["uniqueLimitsByHero"] = limits;
            }
            json["maxFactions"] = f.MaxFactions;
            json["sets"] = new JsonObject
            {
                ["allowed"] = new JsonArray(f.AllowedSets.Select(s => (JsonNode)s).ToArray()),
                ["forbidden"] = new JsonArray(f.ForbiddenSets.Select(s => (JsonNode)s).ToArray()),
            };
            json["bannedCards"] = f.AllowBanned ? new JsonArray() : new JsonArray(f.EffectiveBanned.Select(s => (JsonNode)s).ToArray());
            json["suspendedCards"] = f.AllowSuspended ? new JsonArray() : new JsonArray(f.EffectiveSuspended.Select(s => (JsonNode)s).ToArray());
            json["uniqueAllowlist"] = f.UniqueAllowlistRefs is { } allow ? new JsonArray(allow.Order().Select(s => (JsonNode)s).ToArray()) : null;
            json["season"] = f.Season?.Code;
            json["seasons"] = new JsonArray(f.Seasons.Select(s => (JsonNode)new JsonObject
            {
                ["code"] = s.Code,
                ["label"] = s.Label,
                ["start"] = s.Start?.ToString("O"),
                ["end"] = s.End?.ToString("O"),
            }).ToArray());
            result.Add(json);
        }
        return Results.Json(result);
    }

    /// <summary>GET /api/me — same shape as the old API.</summary>
    private static IResult Me(HttpContext http)
    {
        var user = http.CurrentUser()!;
        return Results.Json(new { email = user.Email, uniqueId = user.KeycloakId, nickName = user.Username, locale = user.Locale });
    }
}
