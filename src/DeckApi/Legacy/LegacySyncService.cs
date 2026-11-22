using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Decks;
using DeckApi.Formats;
using DeckApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Legacy;

/// <summary>
/// Two-way synchronisation with the old PHP decks-api, always on behalf of a user (their bearer is
/// forwarded, the old API only lets a user see their own decks).
///
/// <list type="bullet">
/// <item><b>Write-through</b> (<see cref="PushCreateAsync"/>, <see cref="PushUpdateAsync"/>,
/// <see cref="PushDeleteAsync"/>): every local write is replayed on the old API with a short timeout. On
/// failure the deck is flagged <c>LegacySyncPending</c> (or a <see cref="LegacyPendingDelete"/> is
/// recorded) and retried on the user's next sync.</item>
/// <item><b>Pull</b> (<see cref="SyncUserAsync"/>, run before listing a user's decks): decks created or
/// modified on the old API since the last watermark are imported/updated; decks missing there are
/// archived here (never deleted), and restored if they show up again; pending local changes are pushed.</item>
/// </list>
/// A deck first seen on the old API keeps its id here (<c>Id = LegacyId</c>) unless that id is taken.
/// Upvotes and view counts are not synchronised.
/// </summary>
public sealed class LegacySyncService(
    DeckDbContext db,
    LegacyDecksClient legacy,
    DeckValidationService validation,
    IFormatCatalog formats,
    ICardCatalog catalog,
    TimeProvider clock,
    IConfiguration config,
    ILogger<LegacySyncService> logger)
{
    private int MaxPullsPerSync => config.GetValue("Legacy:MaxPullsPerSync", 50);

    public bool Enabled => legacy.Enabled;

    /// <summary>The old API stores concrete prints: canonical lines are sent with the deck's overrides applied.</summary>
    public static LegacyDeckWrite ToLegacyBody(Deck deck, IReadOnlyList<DeckLine> canonicalLines) => new(
        deck.Name,
        deck.Description,
        deck.Format,
        deck.IsPublic,
        deck.IsDraft,
        DeckCanonicalizer.Materialize(canonicalLines, deck.AltArtOverrides).Select(l => new LegacyCardLine(l.Reference, l.Quantity)).ToList(),
        deck.AlteredId);

    /// <summary>Creates the deck on the old API. Sets <c>LegacyId</c> or flags the deck pending. Does not save.</summary>
    public async Task PushCreateAsync(Deck deck, IReadOnlyList<DeckLine> lines, string token, CancellationToken ct)
    {
        if (!Enabled) return;
        var result = await legacy.CreateAsync(ToLegacyBody(deck, lines), token, ct);
        if (result is { Ok: true, Value: { } created })
        {
            deck.LegacyId = created.Id;
            deck.LegacyUpdatedAt = created.Stamp;
            deck.LegacySyncPending = false;
        }
        else
        {
            deck.LegacySyncPending = true;
        }
    }

    /// <summary>Replays an update on the old API (creating the deck there if it does not exist). Does not save.</summary>
    public async Task PushUpdateAsync(Deck deck, IReadOnlyList<DeckLine> lines, string token, CancellationToken ct)
    {
        if (!Enabled) return;
        if (deck.LegacyDeletedAt is not null)
        {
            // Gone from the old API: stays local until the owner unarchives it (isDraft: false clears the flag and recreates it there).
            deck.LegacySyncPending = false;
            return;
        }
        if (deck.LegacyId is not { } legacyId)
        {
            await PushCreateAsync(deck, lines, token, ct);
            return;
        }

        var body = ToLegacyBody(deck, lines);
        var result = await legacy.UpdateAsync(legacyId, body, token, ct);
        if (result.Status == LegacyCallStatus.NotFound)
        {
            // Deleted on the old API meanwhile: local write wins, recreate it there.
            deck.LegacyId = null;
            await PushCreateAsync(deck, lines, token, ct);
            return;
        }
        if (result is { Ok: true, Value: { } updated })
        {
            deck.LegacyUpdatedAt = updated.Stamp;
            deck.LegacySyncPending = false;
        }
        else
        {
            deck.LegacySyncPending = true;
        }
    }

    /// <summary>Deletes on the old API, or records the deletion for a later retry. Does not save.</summary>
    public async Task PushDeleteAsync(Deck deck, string token, CancellationToken ct)
    {
        if (!Enabled || deck.LegacyId is not { } legacyId || deck.LegacyDeletedAt is not null) return;
        var status = await legacy.DeleteAsync(legacyId, token, ct);
        if (status == LegacyCallStatus.Failed)
            db.LegacyPendingDeletes.Add(new LegacyPendingDelete { LegacyId = legacyId, UserId = deck.UserId, CreatedAt = clock.GetUtcNow() });
    }

    /// <summary>Pulls the user's decks from the old API and pushes pending local changes. Never throws on old-API failure.</summary>
    public async Task SyncUserAsync(User user, string token, CallerIdentity caller, CancellationToken ct)
    {
        if (!Enabled) return;

        var listed = await legacy.ListMineAsync(token, ct);
        if (!listed.Ok || listed.Value is null) return; // old API down: serve local data only

        var remote = listed.Value.ToDictionary(d => d.Id);
        var local = await db.Decks.Where(d => d.UserId == user.Id).ToListAsync(ct);
        var byLegacyId = local.Where(d => d.LegacyId is not null).ToDictionary(d => d.LegacyId!.Value);

        // 1. Deletions recorded while the old API was down.
        var pendingDeletes = await db.LegacyPendingDeletes.Where(p => p.UserId == user.Id).ToListAsync(ct);
        foreach (var pending in pendingDeletes)
        {
            var status = remote.ContainsKey(pending.LegacyId) ? await legacy.DeleteAsync(pending.LegacyId, token, ct) : LegacyCallStatus.NotFound;
            if (status == LegacyCallStatus.Failed) continue;
            remote.Remove(pending.LegacyId);
            db.LegacyPendingDeletes.Remove(pending);
        }

        // 2. Listed again after having been missing (partial listing, or restored there): takes back the old API's state.
        foreach (var deck in local.Where(d => d.LegacyDeletedAt is not null && remote.ContainsKey(d.LegacyId!.Value)))
        {
            logger.LogInformation("Deck {DeckId} is back on the old API, restoring it", deck.Id);
            deck.IsDraft = remote[deck.LegacyId!.Value].IsDraft;
            SealedDecks.KeepArchived(deck);
            deck.LegacyDeletedAt = null;
        }

        // 3. Created/modified on the old API.
        var pulls = 0;
        foreach (var remoteDeck in remote.Values.OrderByDescending(d => d.Stamp))
        {
            byLegacyId.TryGetValue(remoteDeck.Id, out var deck);
            if (deck is not null && deck.LegacyUpdatedAt is { } seen && remoteDeck.Stamp <= seen) continue;
            // Both sides changed: last writer wins.
            if (deck is { LegacySyncPending: true } && deck.UpdatedAt >= remoteDeck.Stamp) continue;
            if (pulls++ >= MaxPullsPerSync) break;

            var detail = await legacy.GetAsync(remoteDeck.Id, token, ct);
            if (!detail.Ok || detail.Value is null) continue;
            await ApplyRemoteAsync(user, deck, detail.Value, caller, ct);
        }

        // 4. Missing from the old API's listing (only decks known to have been synced and without pending local
        //    work). Archived (isDraft: hidden in BGA, still listed and shared) rather than deleted: a listing that
        //    is partial one day (pagination, bug) must not destroy decks. Step 2 restores them if they show up again.
        var now = clock.GetUtcNow();
        foreach (var deck in local.Where(d => d.LegacyId is { } id && !remote.ContainsKey(id) && !d.LegacySyncPending && d.LegacyDeletedAt is null))
        {
            if (pendingDeletes.Any(p => p.LegacyId == deck.LegacyId)) continue;
            logger.LogInformation("Deck {DeckId} missing from the old API, archiving it", deck.Id);
            deck.LegacyDeletedAt = now;
            deck.IsDraft = true;
        }

        // 5. Local changes made while the old API was down.
        foreach (var deck in local.Where(d => d.LegacySyncPending))
            await PushUpdateAsync(deck, DeckContent.Read(deck), token, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            // Concurrent sync of the same user (unique LegacyId / AlteredId): the other request wins.
            logger.LogWarning(e, "Legacy sync of user {UserId} conflicted, ignoring", user.Id);
            db.ChangeTracker.Clear();
        }
    }

    private async Task ApplyRemoteAsync(User user, Deck? deck, LegacyDeck remote, CallerIdentity caller, CancellationToken ct)
    {
        if (deck is null)
        {
            var idTaken = await db.Decks.AnyAsync(d => d.Id == remote.Id, ct);
            deck = new Deck
            {
                Id = idTaken ? Guid.CreateVersion7() : remote.Id,
                LegacyId = remote.Id,
                UserId = user.Id,
                Name = remote.Name,
                Compressed = "",
                CreatedAt = remote.CreatedAt,
            };
            db.Decks.Add(deck);
        }

        deck.Name = remote.Name;
        deck.Description = remote.Description;
        deck.Format = formats.Find(remote.Format)?.Code ?? remote.Format;
        deck.IsPublic = remote.IsPublic;
        deck.IsDraft = remote.IsDraft;
        SealedDecks.KeepArchived(deck); // not pushed back: the old API keeps its own isDraft until the next local write
        deck.UpdatedAt = remote.UpdatedAt;
        deck.LegacyUpdatedAt = remote.Stamp;
        deck.LegacySyncPending = false;
        if (remote.AlteredId != deck.AlteredId)
        {
            var conflict = remote.AlteredId is not null && await db.Decks.AnyAsync(d => d.AlteredId == remote.AlteredId && d.Id != deck.Id, ct);
            deck.AlteredId = conflict ? deck.AlteredId : remote.AlteredId;
        }

        // The old API's list (concrete prints) is the truth for cards and prints; token choices are kept.
        var canonical = DeckCanonicalizer.Canonicalize(remote.Lines(), null, deck.AltArtOverrides?.Tokens, catalog);
        deck.AltArtOverrides = canonical.Overrides;
        await validation.ApplyAsync(deck, canonical.Lines, caller, ct);
    }
}
