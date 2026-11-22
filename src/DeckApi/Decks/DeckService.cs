using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Formats;
using DeckApi.Legacy;
using DeckApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Decks;

public sealed record WriteResult(Deck? Deck, List<InputViolation> Violations, int Status = 200)
{
    public static WriteResult Invalid(List<InputViolation> v) => new(null, v, 422);
}

/// <summary>Deck writes: validation of the input, content/legality computation, persistence, old-API write-through.</summary>
public sealed class DeckService(DeckDbContext db, DeckValidationService validation, LegacySyncService legacy, ICardCatalog catalog, TimeProvider clock)
{
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)); // old API stored timestamp(0)
    }

    public async Task<WriteResult> CreateAsync(User user, DeckWriteRequest request, CallerIdentity caller, CancellationToken ct)
    {
        var canonical = DeckCanonicalizer.Canonicalize(
            request.DeckCards.HasValue ? request.DeckCards.Value : [],
            request.AltArtOverrides.HasValue ? request.AltArtOverrides.Value : null,
            tokens: null, catalog);
        var lines = canonical.Lines;
        var deck = new Deck
        {
            UserId = user.Id,
            Name = request.Name.Value,
            Description = request.Description.HasValue ? request.Description.Value : null,
            Format = request.Format.HasValue ? request.Format.Value?.Code : null,
            IsPublic = request.IsPublic.HasValue && request.IsPublic.Value,
            IsDraft = request.IsDraft.HasValue && request.IsDraft.Value,
            AlteredId = request.AlteredId.HasValue ? request.AlteredId.Value : null,
            AltArtOverrides = canonical.Overrides,
            Compressed = "",
            CreatedAt = Now(),
        };
        SealedDecks.KeepArchived(deck);

        var violations = await CheckAsync(deck, lines);
        if (violations.Count > 0) return WriteResult.Invalid(violations);

        await validation.ApplyAsync(deck, lines, caller, ct);
        if (caller.BearerToken is { } token) await legacy.PushCreateAsync(deck, lines, token, ct);
        else deck.LegacySyncPending = legacy.Enabled;

        db.Decks.Add(deck);
        return await SaveAsync(deck, 201, ct);
    }

    public async Task<WriteResult> UpdateAsync(Deck deck, DeckWriteRequest request, CallerIdentity caller, CancellationToken ct)
    {
        var current = DeckContent.Read(deck);
        if (request.DeckCards.HasValue && request.DeckCards.Value.Count == 0 && current.Count > 0)
            return WriteResult.Invalid([new("deckCards", "deckCards cannot be empty. Send at least one card or omit the field.")]);

        var newFormat = request.Format.HasValue ? request.Format.Value?.Code : deck.Format;
        if (newFormat != deck.Format && (deck.Format == SealedDecks.Format || newFormat == SealedDecks.Format))
            return WriteResult.Invalid([new("format", "Cannot change the format of a sealed deck.")]);

        if (request.Name.HasValue) deck.Name = request.Name.Value;
        if (request.Description.HasValue) deck.Description = request.Description.Value;
        deck.Format = newFormat;
        if (request.IsPublic.HasValue) deck.IsPublic = request.IsPublic.Value;
        if (request.IsDraft.HasValue)
        {
            deck.IsDraft = request.IsDraft.Value;
            // Unarchiving a deck that disappeared from the old API recreates it there (PushUpdateAsync below).
            if (!deck.IsDraft) deck.LegacyDeletedAt = null;
        }
        SealedDecks.KeepArchived(deck);
        if (request.AlteredId.HasValue) deck.AlteredId = request.AlteredId.Value;
        deck.UpdatedAt = Now();

        // A sent card list is the whole truth for cards and their prints (old clients send concrete prints and
        // never the overrides field); overrides sent alongside apply on top. Token choices survive unless resent.
        var lines = current;
        if (request.DeckCards.HasValue || request.AltArtOverrides.HasValue)
        {
            var canonical = DeckCanonicalizer.Canonicalize(
                request.DeckCards.HasValue ? request.DeckCards.Value : current,
                request.AltArtOverrides.HasValue ? request.AltArtOverrides.Value ?? new AltArtOverrides()
                    : request.DeckCards.HasValue ? null : deck.AltArtOverrides,
                deck.AltArtOverrides?.Tokens, catalog);
            lines = canonical.Lines;
            deck.AltArtOverrides = canonical.Overrides;
        }

        var violations = await CheckAsync(deck, lines);
        if (violations.Count > 0) return WriteResult.Invalid(violations);

        await validation.ApplyAsync(deck, lines, caller, ct);
        if (caller.BearerToken is { } token) await legacy.PushUpdateAsync(deck, lines, token, ct);
        else deck.LegacySyncPending = legacy.Enabled;

        return await SaveAsync(deck, 200, ct);
    }

    public async Task DeleteAsync(Deck deck, CallerIdentity caller, CancellationToken ct)
    {
        if (caller.BearerToken is { } token) await legacy.PushDeleteAsync(deck, token, ct);
        db.Decks.Remove(deck);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Validates the user's decks never validated yet (<c>ValidatedAt</c> null): sealed decks imported from the old
    /// API (their pool needs the owner's token), or decks written while the card catalog was unavailable. The old
    /// API's own legality is never trusted. Run on the owner's listing, and on reading one deck (<paramref name="deckId"/>:
    /// BGA gets sealed decks from altered-draft, never from the listing, then loads them by id with the player's bearer and
    /// refuses an illegal sealed deck). Bounded per call; the rest waits for the next listing.
    /// </summary>
    public async Task RevalidatePendingAsync(User user, CallerIdentity caller, CancellationToken ct, Guid? deckId = null)
    {
        var query = db.Decks.Where(d => d.UserId == user.Id && d.Source == DeckSource.User && d.ValidatedAt == null);
        if (deckId is { } id) query = query.Where(d => d.Id == id || d.LegacyId == id);
        var pending = await query
            .OrderBy(d => d.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        foreach (var deck in pending)
            await validation.RevalidateAsync(deck, caller, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<InputViolation>> CheckAsync(Deck deck, List<DeckLine> lines)
    {
        var violations = new List<InputViolation>();
        if (deck.AltArtOverrides is { } overrides)
            violations.AddRange(AltArtOverridesRules.Validate(overrides, lines));
        if (deck.AlteredId is { } alteredId && await db.Decks.AnyAsync(d => d.AlteredId == alteredId && d.Id != deck.Id))
            violations.Add(new("alteredId", "This deck already exists."));
        return violations;
    }

    private async Task<WriteResult> SaveAsync(Deck deck, int status, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (deck.AlteredId is not null)
        {
            return WriteResult.Invalid([new("alteredId", "This deck already exists.")]);
        }
        return new WriteResult(deck, [], status);
    }
}
