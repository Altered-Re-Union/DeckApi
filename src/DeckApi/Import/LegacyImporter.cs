using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Decks;
using DeckApi.Formats;
using DeckApi.Validation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DeckApi.Import;

/// <summary>
/// Imports a restored backup of the old PHP decks-api database (tables <c>"user"</c>, <c>deck</c>,
/// <c>deck_card</c>, <c>deck_upvote</c>) into this API's database.
///
/// Idempotent and re-runnable: users are matched by Keycloak id, decks by legacy id. Decks keep their old
/// id (<c>Id = LegacyId</c>) so links, BGA and altered-draft references keep working. A deck already
/// present is only overwritten when the backup has a newer <c>updated_at</c> than what was last synced.
/// Content is re-encoded (Deckfmt) and legality recomputed with the new rules; the backup's <c>legal</c>
/// column is never used. Sealed decks (pool readable only with the owner's token) are imported illegal and
/// pending, and validated on the owner's next deck listing (<see cref="DeckService.RevalidatePendingAsync"/>).
///
/// Run: <c>dotnet DeckApi.dll import-legacy --source "Host=...;Database=legacy;..." [--dry-run]</c>
/// (target = <c>ConnectionStrings:decks</c>), or <c>scripts/import-legacy.ps1|sh</c> from a dump file.
/// </summary>
public sealed class LegacyImporter(
    DeckDbContext db,
    DeckValidationService validation,
    IFormatCatalog formats,
    CardCatalogRefresher catalogLoader,
    ICardCatalog catalog,
    ILogger<LegacyImporter> logger)
{
    public sealed record Report(int Users, int DecksCreated, int DecksUpdated, int DecksUnchanged, int Upvotes, int DecksUncompressible);

    public async Task<Report> RunAsync(string sourceConnection, bool dryRun, CancellationToken ct)
    {
        await catalogLoader.LoadSnapshotAsync(ct);
        if (!catalog.IsLoaded && !await catalogLoader.RefreshAsync(ct))
            throw new InvalidOperationException("Card catalog unavailable (no snapshot and uniques-search-api unreachable): cannot compute hero/legality.");

        await using var source = new NpgsqlConnection(sourceConnection);
        await source.OpenAsync(ct);

        // ---- Users ----------------------------------------------------------------------------------
        var userIdMap = new Dictionary<Guid, Guid>(); // legacy user id -> local user id
        var users = 0;
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, keycloak_id, email, username, created_at, updated_at, is_admin, locale FROM \"user\"", source))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            var existing = await db.Users.ToDictionaryAsync(u => u.KeycloakId, ct);
            while (await reader.ReadAsync(ct))
            {
                var legacyId = reader.GetGuid(0);
                var keycloakId = reader.GetString(1);
                if (!existing.TryGetValue(keycloakId, out var user))
                {
                    user = new User
                    {
                        Id = legacyId,
                        KeycloakId = keycloakId,
                        CreatedAt = Utc(reader.GetDateTime(4)),
                    };
                    db.Users.Add(user);
                    existing[keycloakId] = user;
                }
                user.Email ??= reader.IsDBNull(2) ? null : reader.GetString(2);
                // The old API may have stored an email as username: never imported as the public pseudo.
                user.Username ??= reader.IsDBNull(3) || reader.GetString(3).Contains('@') ? null : reader.GetString(3);
                user.UpdatedAt ??= reader.IsDBNull(5) ? null : Utc(reader.GetDateTime(5));
                user.IsAdmin |= reader.GetBoolean(6);
                user.Locale ??= reader.IsDBNull(7) ? null : reader.GetString(7);
                userIdMap[legacyId] = user.Id;
                users++;
            }
        }
        if (!dryRun) await db.SaveChangesAsync(ct);
        logger.LogInformation("Imported {Users} users", users);

        // ---- Decks ----------------------------------------------------------------------------------
        int created = 0, updated = 0, unchanged = 0, uncompressible = 0;
        const string deckSql = """
            SELECT d.id, d.user_id, d.name, d.description, d.format, d.is_public, d.is_draft, d.created_at, d.updated_at,
                   d.altered_id, d.view_count, d.upvote_count,
                   COALESCE((SELECT json_agg(json_build_array(c.card_reference, c.quantity) ORDER BY c.id)
                             FROM deck_card c WHERE c.deck_id = d.id)::text, '[]')
            FROM deck d
            ORDER BY d.created_at, d.id
            """;
        var localDecks = await db.Decks.Select(d => new { d.Id, d.LegacyId, d.LegacyUpdatedAt }).ToListAsync(ct);
        var byLegacyId = localDecks.Where(d => d.LegacyId != null).ToDictionary(d => d.LegacyId!.Value);
        var takenIds = localDecks.Select(d => d.Id).ToHashSet();
        var takenAlteredIds = (await db.Decks.Where(d => d.AlteredId != null).Select(d => d.AlteredId!).ToListAsync(ct)).ToHashSet();

        await using (var cmd = new NpgsqlCommand(deckSql, source))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            var pending = 0;
            while (await reader.ReadAsync(ct))
            {
                var legacyId = reader.GetGuid(0);
                if (!userIdMap.TryGetValue(reader.GetGuid(1), out var userId)) continue;
                var createdAt = Utc(reader.GetDateTime(7));
                DateTimeOffset? updatedAt = reader.IsDBNull(8) ? null : Utc(reader.GetDateTime(8));
                var stamp = updatedAt ?? createdAt;

                Deck deck;
                if (byLegacyId.TryGetValue(legacyId, out var known))
                {
                    if (known.LegacyUpdatedAt is { } seen && stamp <= seen) { unchanged++; continue; }
                    deck = await db.Decks.FirstAsync(d => d.Id == known.Id, ct);
                    updated++;
                }
                else
                {
                    deck = new Deck
                    {
                        Id = takenIds.Add(legacyId) ? legacyId : Guid.CreateVersion7(),
                        LegacyId = legacyId,
                        UserId = userId,
                        Name = "",
                        Compressed = "",
                        CreatedAt = createdAt,
                    };
                    db.Decks.Add(deck);
                    created++;
                }

                deck.Name = reader.GetString(2);
                deck.Description = reader.IsDBNull(3) ? null : reader.GetString(3);
                var rawFormat = reader.IsDBNull(4) ? null : reader.GetString(4);
                deck.Format = formats.Find(rawFormat)?.Code ?? rawFormat;
                deck.IsPublic = reader.GetBoolean(5);
                deck.IsDraft = reader.GetBoolean(6);
                SealedDecks.KeepArchived(deck);
                deck.UpdatedAt = updatedAt;
                deck.LegacyUpdatedAt = stamp;
                deck.LegacySyncPending = false;
                deck.ViewCount = reader.GetInt32(10);
                deck.UpvoteCount = reader.GetInt32(11);
                var alteredId = reader.IsDBNull(9) ? null : reader.GetString(9);
                if (alteredId is not null && alteredId != deck.AlteredId && !takenAlteredIds.Add(alteredId)) alteredId = deck.AlteredId;
                deck.AlteredId = alteredId;

                // Alt-art/promo prints stored in the old list become overrides of the canonical cards.
                var canonical = DeckCanonicalizer.Canonicalize(ParseCards(reader.GetString(12)), null, deck.AltArtOverrides?.Tokens, catalog);
                deck.AltArtOverrides = canonical.Overrides;
                var outcome = await validation.ApplyAsync(deck, canonical.Lines, caller: null, ct);
                if (outcome == ValidationOutcome.SkippedNeedsCaller)
                {
                    // Sealed: the pool needs the owner's token. The old API's verdict is not trusted: illegal and
                    // pending (ValidatedAt null) until the owner's next GET /api/decks revalidates it.
                    deck.Legal = false;
                    deck.ValidationErrors = null;
                    deck.LegalityDetail = null;
                    deck.ValidatedAt = null;
                }
                if (deck.ExtraCards is { Count: > 0 })
                {
                    uncompressible++;
                    logger.LogWarning("Deck {DeckId}: {Count} line(s) not encodable with Deckfmt, kept uncompressed: {Refs}",
                        deck.Id, deck.ExtraCards.Count, string.Join(", ", deck.ExtraCards.Select(l => l.Reference)));
                }

                if (++pending >= 500)
                {
                    if (!dryRun) await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    pending = 0;
                    logger.LogInformation("Decks: {Created} created, {Updated} updated so far", created, updated);
                }
            }
        }
        if (!dryRun) await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // ---- Upvotes --------------------------------------------------------------------------------
        var upvotes = 0;
        if (!dryRun)
        {
            var deckIdByLegacy = await db.Decks.Where(d => d.LegacyId != null).ToDictionaryAsync(d => d.LegacyId!.Value, d => d.Id, ct);
            var existingVotes = (await db.DeckUpvotes.Select(u => new { u.DeckId, u.UserId }).ToListAsync(ct))
                .Select(u => (u.DeckId, u.UserId)).ToHashSet();
            await using var cmd = new NpgsqlCommand("SELECT deck_id, user_id, created_at FROM deck_upvote", source);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!deckIdByLegacy.TryGetValue(reader.GetGuid(0), out var deckId)) continue;
                if (!userIdMap.TryGetValue(reader.GetGuid(1), out var userId)) continue;
                if (!existingVotes.Add((deckId, userId))) continue;
                db.DeckUpvotes.Add(new DeckUpvote { DeckId = deckId, UserId = userId, CreatedAt = Utc(reader.GetDateTime(2)) });
                upvotes++;
            }
            await db.SaveChangesAsync(ct);
        }

        var report = new Report(users, created, updated, unchanged, upvotes, uncompressible);
        logger.LogInformation("Import done{DryRun}: {@Report}", dryRun ? " (dry run, nothing written)" : "", report);
        return report;
    }

    private static List<DeckLine> ParseCards(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement[]>>(json)!
            .Select(a => new DeckLine(a[0].GetString()!, a[1].GetInt32()))
            .ToList();

    /// <summary>Old API columns are <c>timestamp(0) without time zone</c> holding UTC.</summary>
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
