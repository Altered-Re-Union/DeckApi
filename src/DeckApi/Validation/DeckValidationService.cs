using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Decks;
using DeckApi.Formats;

namespace DeckApi.Validation;

/// <summary>Identity of the HTTP caller, needed for sealed pool lookups (bearer forwarded to altered-draft).</summary>
public sealed record CallerIdentity(string Sub, string? BearerToken);

public enum ValidationOutcome { Validated, Cleared, CatalogUnavailable, SkippedNeedsCaller }

public sealed class DeckValidationService(ICardCatalog catalog, IFormatCatalog formats, AlteredDraftSealedPoolClient pools, TimeProvider clock)
{
    /// <summary>
    /// Rewrites the deck's content fields from <paramref name="lines"/> and recomputes its legality.
    /// Drafts are validated like any other deck (unlike the old API, <c>isDraft</c> only hides the deck in BGA).
    /// No/unknown format → cleared (legal=false); card catalog unavailable → content saved, legality left unchanged.
    /// Sealed decks need the owner's bearer: without <paramref name="caller"/>, legality is left unchanged.
    /// </summary>
    public async Task<ValidationOutcome> ApplyAsync(Deck deck, IReadOnlyList<DeckLine> lines, CallerIdentity? caller, CancellationToken ct)
    {
        IReadOnlyDictionary<string, ResolvedCard>? resolved = null;
        try
        {
            resolved = await catalog.ResolveAsync(lines.Select(l => l.Reference), ct);
        }
        catch (CatalogUnavailableException)
        {
        }

        DeckContent.Apply(deck, lines, resolved);
        return await ValidateAsync(deck, lines, resolved, caller, ct);
    }

    /// <summary>Recomputes legality only (content unchanged), e.g. after a ban-list change.</summary>
    public async Task<ValidationOutcome> RevalidateAsync(Deck deck, CallerIdentity? caller, CancellationToken ct)
    {
        var lines = DeckContent.Read(deck);
        IReadOnlyDictionary<string, ResolvedCard>? resolved = null;
        try
        {
            resolved = await catalog.ResolveAsync(lines.Select(l => l.Reference), ct);
            // Hero detection may have changed with a newer catalog.
            DeckContent.Apply(deck, lines, resolved);
        }
        catch (CatalogUnavailableException)
        {
        }
        return await ValidateAsync(deck, lines, resolved, caller, ct);
    }

    private async Task<ValidationOutcome> ValidateAsync(Deck deck, IReadOnlyList<DeckLine> lines, IReadOnlyDictionary<string, ResolvedCard>? resolved, CallerIdentity? caller, CancellationToken ct)
    {
        var format = formats.Find(deck.Format);
        if (format is null)
        {
            deck.ValidationErrors = null;
            deck.LegalityDetail = null;
            deck.Legal = false;
            deck.ValidatedAt = clock.GetUtcNow();
            return ValidationOutcome.Cleared;
        }

        if (resolved is null) return ValidationOutcome.CatalogUnavailable;

        IReadOnlyDictionary<string, int>? pool = null;
        if (format.Special == "sealed")
        {
            if (caller is null) return ValidationOutcome.SkippedNeedsCaller;
            // altered-draft links pools to DeckApi ids (imported decks kept their old-API id); the old-API id is a fallback.
            Guid[] ids = deck.LegacyId is { } legacyId ? [deck.Id, legacyId] : [deck.Id];
            pool = CanonicalPool(await pools.GetPoolCountsAsync(ids, caller.Sub, caller.BearerToken, ct), catalog);
        }

        var result = DeckValidator.Validate(format, lines, resolved, pool);
        deck.ValidationErrors = result.Errors.Count > 0 ? result.Errors : null;
        deck.LegalityDetail = result.Detail;
        deck.Legal = result.Legal;
        deck.ValidatedAt = clock.GetUtcNow();
        if (result.Legal && format.Season is { } season) AddSeason(deck, format.Code, season.Code);
        return ValidationOutcome.Validated;
    }

    /// <summary>
    /// altered-draft lists the prints actually opened (e.g. <c>ALT_ALIZE_B_AX_06_C</c>), while stored deck lines are
    /// canonical (<c>ALT_CORE_B_AX_06_C</c>, see <see cref="DeckCanonicalizer"/>): pool keys are mapped to the same
    /// canonical references, copies of one card summed across its prints, so reprints (heroes first) match.
    /// </summary>
    public static IReadOnlyDictionary<string, int>? CanonicalPool(IReadOnlyDictionary<string, int>? pool, ICardCatalog catalog)
    {
        if (pool is null) return null;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (reference, count) in pool)
        {
            if (count <= 0) continue;
            var canonical = catalog.CanonicalReference(reference) ?? reference;
            result[canonical] = result.GetValueOrDefault(canonical) + count;
        }
        return result;
    }

    /// <summary>
    /// Data recovery: checks the deck against every started season of its format, newest first, and records
    /// those it is legal in. Seasons that ended before the deck was created are skipped. Sealed decks are skipped
    /// (no seasons, pool only readable with the owner's token). Returns the number of seasons added.
    /// </summary>
    public async Task<int> BackfillSeasonsAsync(Deck deck, CancellationToken ct)
    {
        var seasons = formats.SeasonsOf(deck.Format);
        if (seasons.Count == 0 || seasons[0].Special == "sealed") return 0;

        var lines = DeckContent.Read(deck);
        IReadOnlyDictionary<string, ResolvedCard> resolved;
        try
        {
            resolved = await catalog.ResolveAsync(lines.Select(l => l.Reference), ct);
        }
        catch (CatalogUnavailableException)
        {
            return 0;
        }

        var before = deck.ValidSeasons.Length;
        foreach (var definition in seasons.Reverse())
        {
            var season = definition.Season!;
            if (!season.CouldHaveBeenPlayed(deck.CreatedAt)) continue;
            if (deck.ValidSeasons.Contains(FormatDefinition.SeasonKey(definition.Code, season.Code))) continue;
            if (DeckValidator.Validate(definition, lines, resolved, sealedPool: null).Legal) AddSeason(deck, definition.Code, season.Code);
        }
        return deck.ValidSeasons.Length - before;
    }

    /// <summary>Tournament decks: records the season the deck was played in when it was legal under that season's rules.</summary>
    public async Task<bool> RecordSeasonPlayedAtAsync(Deck deck, DateTimeOffset playedAt, CancellationToken ct)
    {
        if (formats.SeasonAt(deck.Format, playedAt) is not { Season: { } season } definition || definition.Special == "sealed") return false;
        var key = FormatDefinition.SeasonKey(definition.Code, season.Code);
        if (deck.ValidSeasons.Contains(key)) return false;

        var lines = DeckContent.Read(deck);
        IReadOnlyDictionary<string, ResolvedCard> resolved;
        try
        {
            resolved = await catalog.ResolveAsync(lines.Select(l => l.Reference), ct);
        }
        catch (CatalogUnavailableException)
        {
            return false;
        }
        if (!DeckValidator.Validate(definition, lines, resolved, sealedPool: null).Legal) return false;
        AddSeason(deck, definition.Code, season.Code);
        return true;
    }

    private static void AddSeason(Deck deck, string formatCode, string seasonCode)
    {
        var key = FormatDefinition.SeasonKey(formatCode, seasonCode);
        if (!deck.ValidSeasons.Contains(key)) deck.ValidSeasons = [.. deck.ValidSeasons, key];
    }
}
