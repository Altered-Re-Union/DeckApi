using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Decks;
using DeckApi.Formats;
using DeckApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Tournaments;

public sealed record TournamentDeckSyncReport(int Players, int Decks, int SkippedNoReunionAccount, int SkippedUndecodable, int OrphansDeleted, bool Completed);

/// <summary>
/// Imports GameApi's tournament decks as public, read-only <see cref="DeckSource.Tournament"/> decks:
/// <list type="bullet">
/// <item>stored canonical like any deck, so every public filter (hero, faction, card, format, season) applies;</item>
/// <item>owned by the player's ReUnion account (created on the fly with GameApi's pseudo when unknown here),
/// one deck per owner + format + canonical list, with one <see cref="DeckTournamentResult"/> per tournament;</item>
/// <item>players GameApi could not link to a ReUnion account are skipped: their decks are never shown;</item>
/// <item>validated with the current rules, and the season each result was played in is recorded when the list was
/// legal under that season's rules.</item>
/// </list>
/// A GameApi row replaces all the results it produced before; decks left without results are deleted.
/// </summary>
public sealed class TournamentDeckSync(
    DeckDbContext db, GameApiClient gameApi, ICardCatalog catalog, IFormatCatalog formats, DeckValidationService validation,
    TimeProvider clock, IConfiguration config, ILogger<TournamentDeckSync> logger)
{
    public const string CursorName = "gameapi-tournament-decks";

    public async Task<TournamentDeckSyncReport> SyncAsync(CancellationToken ct)
    {
        int players = 0, decks = 0, noAccount = 0, undecodable = 0;
        if (!gameApi.Enabled || !catalog.IsLoaded) return new(0, 0, 0, 0, 0, Completed: false);

        var pageSize = config.GetValue("GameApi:PageSize", 200);
        var cursor = await db.SyncCursors.FirstOrDefaultAsync(c => c.Name == CursorName, ct);
        while (true)
        {
            var page = await gameApi.FetchAsync(cursor?.Value, pageSize, ct);
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                foreach (var player in page.Players)
                {
                    players++;
                    var (imported, skipped) = await ImportPlayerAsync(player, ct);
                    decks += imported;
                    undecodable += skipped;
                    if (player.ReunionUserId is null) noAccount++;
                }

                cursor ??= db.SyncCursors.Add(new SyncCursor { Name = CursorName }).Entity;
                cursor.Value = page.Next ?? cursor.Value;
                cursor.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            db.ChangeTracker.Clear();
            cursor = await db.SyncCursors.FirstAsync(c => c.Name == CursorName, ct);
            if (!page.HasMore || page.Players.Count == 0) break;
        }

        var orphans = await db.Decks.Where(d => d.Source == DeckSource.Tournament && !d.TournamentResults.Any()).ExecuteDeleteAsync(ct);
        logger.LogInformation("Tournament decks synced: {Players} players, {Decks} decks, {NoAccount} without ReUnion account, {Undecodable} undecodable lists, {Orphans} orphans deleted",
            players, decks, noAccount, undecodable, orphans);
        return new(players, decks, noAccount, undecodable, orphans, Completed: true);
    }

    private async Task<(int Imported, int Undecodable)> ImportPlayerAsync(TournamentPlayerDecks player, CancellationToken ct)
    {
        await db.DeckTournamentResults
            .Where(r => r.TournamentParentId == player.TournamentParentId && r.BgaUserId == player.BgaUserId)
            .ExecuteDeleteAsync(ct);
        if (player.ReunionUserId is null) return (0, 0);

        var owner = await OwnerAsync(player.ReunionUserId, player.Pseudo, ct);
        var format = FormatOf(player.Mode);
        int imported = 0, undecodable = 0;
        foreach (var usage in player.Decks)
        {
            List<DeckLine> lines;
            try
            {
                lines = DeckCanonicalizer.Canonicalize(DeckContent.Decode(usage.Deck), null, null, catalog).Lines;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Undecodable tournament list (tournament {Tournament}, player {Player})", player.TournamentParentId, player.BgaUserId);
                undecodable++;
                continue;
            }

            var probe = new Deck { Name = "", Compressed = "" };
            DeckContent.Apply(probe, lines, resolved: null);
            var deck = await db.Decks.Include(d => d.TournamentResults).FirstOrDefaultAsync(d =>
                d.Source == DeckSource.Tournament && d.UserId == owner.Id && d.Format == format && d.Compressed == probe.Compressed, ct);
            if (deck is null)
            {
                var now = clock.GetUtcNow();
                deck = new Deck
                {
                    UserId = owner.Id,
                    Name = player.TournamentName ?? $"Tournament {player.TournamentParentId}",
                    Format = format,
                    IsPublic = true,
                    Source = DeckSource.Tournament,
                    Compressed = "",
                    CreatedAt = usage.LastPlayedAt ?? now,
                };
                db.Decks.Add(deck);
                if (await validation.ApplyAsync(deck, lines, caller: null, ct) == ValidationOutcome.CatalogUnavailable)
                    throw new CatalogUnavailableException();
            }
            else if (usage.LastPlayedAt is { } played && played < deck.CreatedAt)
            {
                deck.CreatedAt = played;
            }

            // Two prints of the same list in one tournament are one canonical deck: merge their records.
            if (deck.TournamentResults.FirstOrDefault(r => r.TournamentParentId == player.TournamentParentId && r.BgaUserId == player.BgaUserId) is { } same)
            {
                same.Games += usage.Games;
                same.Wins += usage.Wins;
                same.Losses += usage.Losses;
                if (usage.LastPlayedAt > same.LastPlayedAt || same.LastPlayedAt is null) same.LastPlayedAt = usage.LastPlayedAt;
            }
            // Added through the DbSet: a client-generated key on a tracked deck's collection would be taken for an update.
            else db.DeckTournamentResults.Add(new DeckTournamentResult
            {
                DeckId = deck.Id,
                TournamentParentId = player.TournamentParentId,
                TournamentName = player.TournamentName,
                Mode = player.Mode,
                BgaUserId = player.BgaUserId,
                Games = usage.Games,
                Wins = usage.Wins,
                Losses = usage.Losses,
                LastPlayedAt = usage.LastPlayedAt,
                ComputedAt = player.ComputedAt,
            });
            deck.UpdatedAt = deck.TournamentResults.Max(r => r.LastPlayedAt) ?? deck.UpdatedAt;
            if (usage.LastPlayedAt is { } playedAt) await validation.RecordSeasonPlayedAtAsync(deck, playedAt, ct);
            await db.SaveChangesAsync(ct);
            imported++;
        }
        return (imported, undecodable);
    }

    /// <summary>The ReUnion account, created with GameApi's pseudo when the player never used this API.</summary>
    private async Task<User> OwnerAsync(string keycloakId, string? pseudo, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.KeycloakId == keycloakId, ct);
        if (user is null)
        {
            user = new User { KeycloakId = keycloakId, Username = pseudo, CreatedAt = clock.GetUtcNow() };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }
        else if (pseudo is not null && user.Username != pseudo)
        {
            user.Username = pseudo;
            user.UpdatedAt = clock.GetUtcNow();
        }
        return user;
    }

    /// <summary>GameApi reports BGA's format (<c>FRONTIER</c>, <c>NO_UNIQUE</c>...): codes and aliases match ours.</summary>
    private string? FormatOf(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? null : (formats.Find(mode) ?? formats.Find(mode.Trim().Replace(' ', '_')))?.Code;
}
