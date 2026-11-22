using DeckApi.Auth;
using DeckApi.Data;
using DeckApi.Formats;
using DeckApi.Tournaments;
using DeckApi.Validation;
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
    /// <c>formats.json</c>. Drafts included. Sealed decks are skipped: their pool can only
    /// be read with the owner's own token. A deck legal in the current season gets it in <c>validSeasons</c>;
    /// <c>seasons=all</c> also checks every past season (data recovery, see <see cref="DeckValidationService.BackfillSeasonsAsync"/>). Returns counters.
    /// </summary>
    private static async Task<IResult> Revalidate(HttpContext http, DeckDbContext db, DeckValidationService validation, IFormatCatalog formats, ILoggerFactory loggers, CancellationToken ct)
    {
        var code = http.Request.Query["format"].ToString();
        var format = formats.Find(code);
        if (format is null) return Problems.BadRequest($"Unknown format \"{code}\". Expected one of: {string.Join(", ", formats.All.Select(f => f.Code))}.");

        var logger = loggers.CreateLogger("DeckApi.Admin.Revalidate");
        var backfill = http.Request.Query["seasons"].ToString() == "all";
        int processed = 0, changed = 0, skipped = 0, unavailable = 0, seasonsAdded = 0;
        Guid? after = null;
        const int batchSize = 200;

        while (true)
        {
            var query = db.Decks.Where(d => d.Format == format.Code);
            if (after is { } last) query = query.Where(d => d.Id.CompareTo(last) > 0);
            var batch = await query.OrderBy(d => d.Id).Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var deck in batch)
            {
                var before = (deck.Legal, System.Text.Json.JsonSerializer.Serialize(deck.ValidationErrors));
                var seasonsBefore = deck.ValidSeasons.Length;
                var outcome = await validation.RevalidateAsync(deck, caller: null, ct);
                if (backfill) await validation.BackfillSeasonsAsync(deck, ct);
                seasonsAdded += deck.ValidSeasons.Length - seasonsBefore;
                processed++;
                switch (outcome)
                {
                    case ValidationOutcome.SkippedNeedsCaller: skipped++; break;
                    case ValidationOutcome.CatalogUnavailable: unavailable++; break;
                    default:
                        if (before != (deck.Legal, System.Text.Json.JsonSerializer.Serialize(deck.ValidationErrors))) changed++;
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            after = batch[^1].Id;
        }

        logger.LogInformation("Revalidated {Format}: {Processed} processed, {Changed} changed, {Skipped} skipped, {Unavailable} catalog unavailable, {SeasonsAdded} seasons added",
            format.Code, processed, changed, skipped, unavailable, seasonsAdded);
        return Results.Json(new { format = format.Code, processed, changed, skipped, catalogUnavailable = unavailable, seasonsAdded });
    }
}
