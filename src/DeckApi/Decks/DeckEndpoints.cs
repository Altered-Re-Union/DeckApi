using System.Text.Json.Nodes;
using DeckApi.Auth;
using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Formats;
using DeckApi.Legacy;
using DeckApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Decks;

/// <summary>
/// <c>/api/decks*</c>, same contract as the old API (paths, status codes, JSON shapes) with additive
/// query parameters and fields only.
/// </summary>
public static class DeckEndpoints
{
    public static void MapDeckEndpoints(this IEndpointRouteBuilder app)
    {
        var decks = app.MapGroup("/api/decks");

        decks.MapGet("", ListMine).RequireAuthorization(AuthPolicies.ReadDecks);
        decks.MapPost("", Create).RequireAuthorization(AuthPolicies.WriteDecks);
        decks.MapGet("/{id:guid}", Get); // public decks stay anonymous; private ones need read-decks (checked inside)
        decks.MapPatch("/{id:guid}", Update).RequireAuthorization(AuthPolicies.WriteDecks);
        decks.MapPut("/{id:guid}", Update).RequireAuthorization(AuthPolicies.WriteDecks);
        decks.MapDelete("/{id:guid}", Delete).RequireAuthorization(AuthPolicies.WriteDecks);
        decks.MapPost("/{id:guid}/upvote", ToggleUpvote).RequireAuthorization(AuthPolicies.User);
    }

    internal static CallerIdentity Caller(HttpContext http, User user) => new(user.KeycloakId, http.BearerToken());

    /// <summary>
    /// GET /api/decks — the caller's decks as a plain array, most recently updated first (never-updated first,
    /// like Postgres' DESC NULLS FIRST in the old API). Pulls changes from the old API first.
    /// Filters: <c>faction</c>, <c>hero</c> (old API); added: <c>format</c> (repeatable or comma-separated,
    /// aliases accepted), <c>legal</c>, <c>isDraft</c>, <c>cardReference</c>, <c>family</c>.
    /// </summary>
    private static async Task<IResult> ListMine(
        HttpContext http, DeckDbContext db, LegacySyncService sync, DeckService service, DeckPresenter presenter, IFormatCatalog formats, ICardCatalog catalog, CancellationToken ct)
    {
        var user = http.CurrentUser()!;
        if (http.BearerToken() is { } token)
        {
            await sync.SyncUserAsync(user, token, Caller(http, user), ct);
            await service.RevalidatePendingAsync(user, Caller(http, user), ct);
        }

        var q = http.Request.Query;
        // Tournament copies are independent of the player's own decks: never listed here (nor in BGA).
        var query = db.Decks.AsNoTracking().Where(d => d.UserId == user.Id && d.Source == DeckSource.User);

        if (Single(q["faction"]) is { } faction) query = query.Where(d => d.HeroFaction == faction);
        if (Single(q["hero"]) is { } hero && HeroFamily(hero) is { } heroFamily)
            query = query.Where(d => d.HeroReference != null && d.CardFamilies.Contains(heroFamily));
        var formatCodes = q["format"].SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(f => formats.Find(f)?.Code ?? f).Distinct().ToList();
        if (formatCodes.Count > 0) query = query.Where(d => d.Format != null && formatCodes.Contains(d.Format));
        if (bool.TryParse(Single(q["legal"]), out var legal)) query = query.Where(d => d.Legal == legal);
        if (bool.TryParse(Single(q["isDraft"]), out var isDraft)) query = query.Where(d => d.IsDraft == isDraft);
        if (Single(q["cardReference"]) is { } rawRef) 
        {
            // Stored lists are canonical: any print of the card (alt art, promo, reprint) finds the decks.
            var cardRef = catalog.CanonicalReference(rawRef) ?? rawRef;
            query = query.Where(d => d.CardReferences.Contains(cardRef));
        }
        if (Single(q["family"]) is { } family) query = query.Where(d => d.CardFamilies.Contains(family.ToUpperInvariant()));

        var list = await query
            .OrderByDescending(d => d.UpdatedAt == null)
            .ThenByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);

        var locale = Single(q["locale"]);
        return Results.Json(new JsonArray(list.Select(d => (JsonNode)presenter.Render(d, locale)).ToArray()));
    }

    /// <summary>GET /api/decks/{id} — public deck to anyone (counts a view), private deck to its owner only. Also accepts an old-API id.</summary>
    private static async Task<IResult> Get(Guid id, HttpContext http, DeckDbContext db, DeckService service, DeckPresenter presenter, IConfiguration config, CancellationToken ct)
    {
        // The owner's own deck still pending validation (sealed deck imported from the old API): validated now, with their bearer.
        if (http.CurrentUser() is { } caller && http.BearerToken() is not null)
            await service.RevalidatePendingAsync(caller, Caller(http, caller), ct, deckId: id);

        var deck = await db.Decks.Include(d => d.User).Include(d => d.TournamentResults).AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id || d.LegacyId == id, ct);
        if (deck is null) return Problems.NotFound();

        // altered-bga-api reads any deck by id, like the old public GET /api/bga/decks/{id} (BGA only knows the id).
        var isService = ApiKeyAuthenticationHandler.Matches(http.Request.Headers[ApiKeyAuthenticationHandler.HeaderName], config["ApiKeys:Bga"]);
        if (isService)
        {
            // No view counted, owner visibility not enforced.
        }
        else if (deck.IsPublic)
        {
            await db.Decks.Where(d => d.Id == deck.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ViewCount, d => d.ViewCount + 1), ct);
            deck.ViewCount++;
        }
        else
        {
            var user = http.CurrentUser();
            if (user is null) return Problems.Unauthorized();
            if (user.Id != deck.UserId || !DeckClaims.CanRead(http.User, config)) return Problems.Forbidden();
        }

        var json = await presenter.RenderDetailAsync(deck, http.Request.Query["locale"].FirstOrDefault(), includeOwner: deck.IsPublic, ct);
        return Results.Json(json);
    }

    private static async Task<IResult> Create(HttpContext http, DeckService service, DeckPresenter presenter, IFormatCatalog formats, CancellationToken ct)
    {
        var user = http.CurrentUser()!;
        if (await ReadBody(http, ct) is not { } body) return Problems.BadRequest("Syntax error: the request body must be a JSON object.");

        var violations = new List<InputViolation>();
        var request = DeckWriteRequest.Parse(body, isCreate: true, formats, violations);
        if (violations.Count > 0) return Problems.Validation(violations);

        var result = await service.CreateAsync(user, request, Caller(http, user), ct);
        if (result.Deck is null) return Problems.Validation(result.Violations);
        return Results.Json(presenter.Render(result.Deck, http.Request.Query["locale"].FirstOrDefault()), statusCode: 201);
    }

    private static async Task<IResult> Update(Guid id, HttpContext http, DeckDbContext db, DeckService service, DeckPresenter presenter, IFormatCatalog formats, CancellationToken ct)
    {
        var user = http.CurrentUser()!;
        var deck = await db.Decks.FirstOrDefaultAsync(d => d.Id == id || d.LegacyId == id, ct);
        if (deck is null) return Problems.NotFound();
        if (deck.UserId != user.Id || deck.Source == DeckSource.Tournament) return Problems.Forbidden();
        if (await ReadBody(http, ct) is not { } body) return Problems.BadRequest("Syntax error: the request body must be a JSON object.");

        var violations = new List<InputViolation>();
        var request = DeckWriteRequest.Parse(body, isCreate: false, formats, violations);
        if (violations.Count > 0) return Problems.Validation(violations);

        var result = await service.UpdateAsync(deck, request, Caller(http, user), ct);
        if (result.Deck is null) return Problems.Validation(result.Violations);
        return Results.Json(presenter.Render(result.Deck, http.Request.Query["locale"].FirstOrDefault()));
    }

    private static async Task<IResult> Delete(Guid id, HttpContext http, DeckDbContext db, DeckService service, CancellationToken ct)
    {
        var user = http.CurrentUser()!;
        var deck = await db.Decks.FirstOrDefaultAsync(d => d.Id == id || d.LegacyId == id, ct);
        if (deck is null) return Problems.NotFound();
        if (deck.UserId != user.Id || deck.Source == DeckSource.Tournament) return Problems.Forbidden();
        await service.DeleteAsync(deck, Caller(http, user), ct);
        return Results.NoContent();
    }

    /// <summary>POST /api/decks/{id}/upvote — toggles the caller's upvote on a public deck.</summary>
    private static async Task<IResult> ToggleUpvote(Guid id, HttpContext http, DeckDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var user = http.CurrentUser()!;
        var deck = await db.Decks.AsNoTracking().FirstOrDefaultAsync(d => (d.Id == id || d.LegacyId == id) && d.IsPublic, ct);
        if (deck is null) return Problems.NotFound();

        var removed = await db.DeckUpvotes.Where(u => u.DeckId == deck.Id && u.UserId == user.Id).ExecuteDeleteAsync(ct);
        bool hasUpvoted;
        if (removed > 0)
        {
            await db.Decks.Where(d => d.Id == deck.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpvoteCount, d => d.UpvoteCount > 0 ? d.UpvoteCount - 1 : 0), ct);
            hasUpvoted = false;
        }
        else
        {
            db.DeckUpvotes.Add(new DeckUpvote { DeckId = deck.Id, UserId = user.Id, CreatedAt = clock.GetUtcNow() });
            try
            {
                await db.SaveChangesAsync(ct);
                await db.Decks.Where(d => d.Id == deck.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.UpvoteCount, d => d.UpvoteCount + 1), ct);
            }
            catch (DbUpdateException)
            {
                // Double click: the concurrent request already upvoted.
            }
            hasUpvoted = true;
        }

        var count = await db.Decks.Where(d => d.Id == deck.Id).Select(d => d.UpvoteCount).FirstAsync(ct);
        return Results.Json(new { hasUpvoted, upvoteCount = count });
    }

    internal static async Task<JsonObject?> ReadBody(HttpContext http, CancellationToken ct)
    {
        try
        {
            return await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal static string? Single(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;

    /// <summary>Old API hero filter: any reference/fragment whose 4th and 5th segments are faction and number (e.g. <c>ALT_CORE_B_AX_01_C</c>) → family.</summary>
    internal static string? HeroFamily(string hero)
    {
        if (CardReference.TryParse(hero, out var parsed)) return parsed.Family;
        var parts = hero.Split('_');
        return parts.Length >= 5 && int.TryParse(parts[4], out var n) ? $"{parts[3]}_{n:00}" : null;
    }
}
