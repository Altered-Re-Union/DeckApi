using System.Text.Json.Nodes;
using DeckApi.Auth;
using DeckApi.Data;
using DeckApi.Decks;
using DeckApi.Formats;
using DeckApi.Tournaments;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/stats", Stats).RequireAuthorization(AuthPolicies.Admin);
        app.MapGet("/api/admin/decks", LatestDecks).RequireAuthorization(AuthPolicies.Admin);

        // Machine-to-machine: X-Api-Key = ApiKeys:Maintenance.
        app.MapPost("/api/admin/decks/revalidate", Revalidate).RequireAuthorization(AuthPolicies.MaintenanceApiKey);
        app.MapPost("/api/admin/tournament-decks/sync", SyncTournamentDecks).RequireAuthorization(AuthPolicies.MaintenanceApiKey);
        app.MapPost("/api/admin/formats/season", SelectSeason).RequireAuthorization(AuthPolicies.MaintenanceApiKey);
        app.MapGet("/api/admin/formats/{format}/season", SeasonStatus).RequireAuthorization(AuthPolicies.MaintenanceApiKey);
    }

    /// <summary>POST /api/admin/tournament-decks/sync — runs the GameApi import now (it also runs every <c>GameApi:SyncMinutes</c>).</summary>
    private static async Task<IResult> SyncTournamentDecks(TournamentDeckSync sync, CancellationToken ct) =>
        Results.Json(await sync.SyncAsync(ct));

    private static async Task<IResult> Stats(DeckDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return Results.Json(new
        {
            decksToday = await db.Decks.CountAsync(d => d.CreatedAt >= today, ct),
            decksThisWeek = await db.Decks.CountAsync(d => d.CreatedAt >= now.AddDays(-7), ct),
            decksTotal = await db.Decks.CountAsync(ct),
        });
    }

    private static async Task<IResult> LatestDecks(DeckDbContext db, CancellationToken ct)
    {
        var decks = await db.Decks.AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(30)
            .Select(d => new { d.Id, d.Name, d.Format, d.IsPublic, d.IsDraft, d.CreatedAt, totalCards = d.CardCount })
            .ToListAsync(ct);
        return Results.Json(decks.Select(d => new
        {
            id = d.Id,
            name = d.Name,
            format = d.Format,
            isPublic = d.IsPublic,
            isDraft = d.IsDraft,
            createdAt = d.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            totalCards = d.totalCards,
        }));
    }

    /// <summary>
    /// POST /api/admin/decks/revalidate?format=standard[&amp;seasons=all] — recomputes legality of every
    /// deck of a format (aliases accepted), e.g. after a ban-list or set rotation change in
    /// <c>formats.json</c>. See <see cref="DeckRevalidator"/>. Returns counters.
    /// </summary>
    private static async Task<IResult> Revalidate(HttpContext http, DeckRevalidator revalidator, IFormatCatalog formats, CancellationToken ct)
    {
        var code = http.Request.Query["format"].ToString();
        var format = formats.Find(code);
        if (format is null) return Problems.BadRequest($"Unknown format \"{code}\". Expected one of: {string.Join(", ", formats.All.Select(f => f.Code))}.");

        var backfill = http.Request.Query["seasons"].ToString() == "all";
        var r = await revalidator.RunAsync(format, backfill, ct);
        return Results.Json(new { format = r.Format, processed = r.Processed, changed = r.Changed, skipped = r.Skipped, catalogUnavailable = r.CatalogUnavailable, seasonsAdded = r.SeasonsAdded });
    }

    /// <summary>
    /// POST /api/admin/formats/season <c>{ "formatId": "frontier_s05" }</c> — called by the website when the Frontier
    /// format is changed on its admin page (core-altered-cards › Uniques format). The season becomes the current one
    /// with the manifest's allowlist, and the format's decks are revalidated in the background (202).
    /// </summary>
    private static async Task<IResult> SelectSeason(HttpContext http, FormatSeasonSelector selector, IFormatCatalog formats, CancellationToken ct)
    {
        JsonObject? body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<JsonObject>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            body = null;
        }
        var id = body?["formatId"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? body["formatId"]!.GetValue<string>() : null;
        if (string.IsNullOrWhiteSpace(id)) return Problems.BadRequest("Body must be { \"formatId\": \"frontier_sNN\" }.");

        try
        {
            var selection = await selector.SelectAsync(id, ct);
            return Results.Json(SeasonJson(formats, selection.Format, revalidation: "queued"), statusCode: StatusCodes.Status202Accepted);
        }
        catch (SeasonSelectionException e)
        {
            return e.Error switch
            {
                SeasonSelectionError.ManifestUnavailable => Problems.Error(StatusCodes.Status502BadGateway, e.Message),
                SeasonSelectionError.NotInManifest or SeasonSelectionError.EmptyAllowlist => Problems.Error(StatusCodes.Status422UnprocessableEntity, e.Message),
                _ => Problems.BadRequest(e.Message),
            };
        }
    }

    /// <summary>GET /api/admin/formats/{format}/season — current season, its source and the last background revalidation.</summary>
    private static async Task<IResult> SeasonStatus(string format, IFormatCatalog formats, DeckDbContext db, RevalidationQueue revalidations, CancellationToken ct)
    {
        if (formats.Find(format) is not { } definition) return Problems.NotFound();
        var json = SeasonJson(formats, definition.Code, revalidations.IsPending(definition.Code) ? "queued" : null);
        var season = definition.Season?.Code;
        var selection = season is null ? null : await db.FormatSeasonSelections.AsNoTracking()
            .Where(s => s.Format == definition.Code && s.Season == season)
            .Select(s => new { s.ManifestId, s.ManifestVersion, s.SelectedAt })
            .FirstOrDefaultAsync(ct);
        json["selection"] = selection is null ? null : new JsonObject
        {
            ["manifestId"] = selection.ManifestId,
            ["manifestVersion"] = selection.ManifestVersion,
            ["selectedAt"] = selection.SelectedAt.ToString("O"),
        };
        if (revalidations.Last.TryGetValue(definition.Code, out var last))
        {
            json["lastRevalidation"] = new JsonObject
            {
                ["finishedAt"] = last.FinishedAt.ToString("O"),
                ["error"] = last.Error,
                ["processed"] = last.Report?.Processed,
                ["changed"] = last.Report?.Changed,
            };
        }
        return Results.Json(json);
    }

    private static JsonObject SeasonJson(IFormatCatalog formats, string code, string? revalidation)
    {
        var current = formats.Find(code)!;
        return new JsonObject
        {
            ["format"] = current.Code,
            ["season"] = current.Season?.Code,
            ["label"] = current.Season?.Label,
            ["start"] = current.Season?.Start?.ToString("O"),
            ["uniqueAllowlistCount"] = current.UniqueAllowlistRefs?.Count,
            ["revalidation"] = revalidation,
        };
    }
}
