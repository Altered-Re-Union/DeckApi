using System.Text.Json.Nodes;
using DeckApi.Auth;
using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Formats;
using DeckApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Decks;

public static class PublicDeckEndpoints
{
    public static void MapPublicDeckEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/decks/public", List);
        app.MapGet("/api/decks/public/heroes", Heroes);
    }

    /// <summary>
    /// GET /api/decks/public — public, legal decks (drafts included: <c>isDraft</c> only hides a deck in BGA). Same pagination body as the old API
    /// (<c>member</c>, <c>totalItems</c>, <c>currentPage</c>, <c>lastPage</c>, <c>nextPage</c>, <c>previousPage</c>).
    /// Each member now carries <c>owner.pseudo</c>. Filters: <c>hero</c>, <c>faction</c>, <c>cardName</c>,
    /// <c>cardReference</c>, <c>name</c> (now case-insensitive), <c>format</c>, <c>order[field]=asc|desc</c>;
    /// added: <c>family</c>. <c>legal</c> is still accepted, but illegal decks are never listed.
    /// <c>source</c>: <c>user</c> (default, the old behaviour), <c>tournament</c> (GameApi copies) or <c>all</c>.
    /// <c>season</c>: <c>frontier:S4</c>, or <c>S4</c> (with <c>format</c>, else the deck's own format).
    /// </summary>
    private static async Task<IResult> List(HttpContext http, DeckDbContext db, DeckPresenter presenter, ICardCatalog catalog, IFormatCatalog formats, CancellationToken ct)
    {
        var q = http.Request.Query;
        var page = Math.Max(1, int.TryParse(q["page"], out var p) ? p : 1);
        var perPage = Math.Clamp(int.TryParse(q["itemsPerPage"], out var ipp) ? ipp : 30, 1, 1000);

        // Tournament decks are records of what was played: listed even when today's rules make them illegal.
        var query = db.Decks.AsNoTracking().Where(d => d.IsPublic && (d.Legal || d.Source == DeckSource.Tournament));
        query = DeckEndpoints.Single(q["source"]) switch
        {
            "all" => query,
            "tournament" => query.Where(d => d.Source == DeckSource.Tournament),
            _ => query.Where(d => d.Source == DeckSource.User),
        };
        if (DeckEndpoints.Single(q["hero"]) is { } hero && DeckEndpoints.HeroFamily(hero) is { } heroFamily)
            query = query.Where(d => d.HeroReference != null && d.CardFamilies.Contains(heroFamily));
        if (DeckEndpoints.Single(q["faction"]) is { } faction) query = query.Where(d => d.HeroFaction == faction);
        if (DeckEndpoints.Single(q["cardReference"]) is { } rawRef) 
        {
            // Stored lists are canonical: any print of the card (alt art, promo, reprint) finds the decks.
            var cardRef = catalog.CanonicalReference(rawRef) ?? rawRef;
            query = query.Where(d => d.CardReferences.Contains(cardRef));
        }
        if (DeckEndpoints.Single(q["family"]) is { } family) query = query.Where(d => d.CardFamilies.Contains(family.ToUpperInvariant()));
        if (DeckEndpoints.Single(q["cardName"]) is { } cardName)
        {
            // Names live in the in-memory card catalog: resolve to families, then use the GIN-indexed array.
            var families = catalog.FamiliesMatchingName(cardName).ToArray();
            query = query.Where(d => d.CardFamilies.Any(f => families.Contains(f)));
        }
        if (DeckEndpoints.Single(q["name"]) is { } name)
            query = query.Where(d => EF.Functions.ILike(d.Name, "%" + EscapeLike(name) + "%"));
        if (DeckEndpoints.Single(q["format"]) is { } format)
        {
            var code = formats.Find(format)?.Code ?? format;
            query = query.Where(d => d.Format == code);
        }
        if (bool.TryParse(DeckEndpoints.Single(q["legal"]), out var legal)) query = query.Where(d => d.Legal == legal);
        if (DeckEndpoints.Single(q["season"]) is { } season)
        {
            // "frontier:S4", or "S4" with a format filter (the format defaults to the deck's own format).
            var key = season.Contains(':') ? season
                : DeckEndpoints.Single(q["format"]) is { } seasonFormat ? FormatDefinition.SeasonKey(formats.Find(seasonFormat)?.Code ?? seasonFormat, season)
                : null;
            query = key is not null
                ? query.Where(d => d.ValidSeasons.Contains(key))
                : query.Where(d => d.Format != null && d.ValidSeasons.Contains(d.Format + ":" + season));
        }

        var total = await query.CountAsync(ct);
        var ordered = ApplyOrder(query, http.Request.Query);
        var decks = await ordered.Include(d => d.User).Include(d => d.TournamentResults).AsSplitQuery().Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);

        var upvoted = new HashSet<Guid>();
        if (http.CurrentUser() is { } user && decks.Count > 0)
        {
            var ids = decks.Select(d => d.Id).ToList();
            upvoted = (await db.DeckUpvotes.Where(u => u.UserId == user.Id && ids.Contains(u.DeckId)).Select(u => u.DeckId).ToListAsync(ct)).ToHashSet();
        }

        var locale = DeckEndpoints.Single(q["locale"]);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)perPage));
        return Results.Json(new JsonObject
        {
            ["member"] = new JsonArray(decks.Select(d => (JsonNode)presenter.Render(d, locale, includeOwner: true, hasUpvoted: upvoted.Contains(d.Id))).ToArray()),
            ["totalItems"] = total,
            ["currentPage"] = page,
            ["lastPage"] = lastPage,
            ["nextPage"] = page < lastPage ? page + 1 : null,
            ["previousPage"] = page > 1 ? page - 1 : null,
        });
    }

    /// <summary>Only the first <c>order[field]</c> key is used; unknown/malformed → <c>createdAt desc</c>. Id as tie-breaker.</summary>
    private static IQueryable<Deck> ApplyOrder(IQueryable<Deck> query, IQueryCollection q)
    {
        var key = q.Keys.FirstOrDefault(k => k.StartsWith("order[", StringComparison.Ordinal) && k.EndsWith(']'));
        var field = key?["order[".Length..^1];
        var asc = key is not null && string.Equals(q[key].ToString(), "asc", StringComparison.OrdinalIgnoreCase);
        IOrderedQueryable<Deck> ordered = field switch
        {
            "name" => asc ? query.OrderBy(d => d.Name) : query.OrderByDescending(d => d.Name),
            "updatedAt" => asc ? query.OrderBy(d => d.UpdatedAt) : query.OrderByDescending(d => d.UpdatedAt),
            "upvoteCount" => asc ? query.OrderBy(d => d.UpvoteCount) : query.OrderByDescending(d => d.UpvoteCount),
            "viewCount" => asc ? query.OrderBy(d => d.ViewCount) : query.OrderByDescending(d => d.ViewCount),
            "createdAt" when asc => query.OrderBy(d => d.CreatedAt),
            _ => query.OrderByDescending(d => d.CreatedAt),
        };
        return ordered.ThenBy(d => d.Id);
    }

    /// <summary>GET /api/decks/public/heroes — one entry per hero family used by a public deck, ordered by faction then number.</summary>
    private static async Task<IResult> Heroes(HttpContext http, DeckDbContext db, DeckPresenter presenter, CancellationToken ct)
    {
        var locale = DeckEndpoints.Single(http.Request.Query["locale"]) ?? DeckPresenter.DefaultCardLocale;
        var heroRefs = await db.Decks.AsNoTracking()
            .Where(d => d.IsPublic && d.Legal && d.HeroReference != null)
            .Select(d => d.HeroReference!)
            .Distinct()
            .ToListAsync(ct);

        var catalog = http.RequestServices.GetRequiredService<ICardCatalog>();
        var heroes = heroRefs
            .Select(r => CardReference.TryParse(r, out var parsed) ? parsed : null)
            .OfType<CardReference>()
            .GroupBy(r => r.Family)
            .Select(g => g.MinBy(r => r.Value, StringComparer.Ordinal)!)
            .OrderBy(r => r.Faction, StringComparer.Ordinal).ThenBy(r => r.Number)
            .Select(r => new
            {
                reference = r.Value,
                name = ValidationMessages.CardName(catalog, r.Value, locale),
                imagePath = presenter.ImagePath(r.Value, locale),
            });
        return Results.Json(heroes);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
