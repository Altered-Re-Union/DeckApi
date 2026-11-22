using System.Text.Json;
using DeckApi.Data;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Cards;

/// <summary>
/// Keeps <see cref="CardCatalog"/> loaded: last good snapshot from the database at startup (so a restart
/// never depends on uniques-search-api), then a full refresh every <c>Cards:RefreshMinutes</c> (default 60),
/// retried every minute while it fails.
/// </summary>
public sealed class CardCatalogRefresher(CardCatalog catalog, IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<CardCatalogRefresher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadSnapshotAsync(stoppingToken);
        var interval = TimeSpan.FromMinutes(config.GetValue("Cards:RefreshMinutes", 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = await RefreshAsync(stoppingToken);
            try
            {
                await Task.Delay(ok ? interval : TimeSpan.FromMinutes(1), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task LoadSnapshotAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeckDbContext>();
            var snapshot = await db.CardCatalogSnapshots.AsNoTracking().FirstOrDefaultAsync(ct);
            if (snapshot is null || catalog.IsLoaded) return;
            var cards = JsonSerializer.Deserialize<List<CatalogCard>>(snapshot.Payload, Json) ?? [];
            catalog.Replace(cards, snapshot.FetchedAt);
            logger.LogInformation("Card catalog loaded from snapshot of {FetchedAt} ({Count} prints)", snapshot.FetchedAt, cards.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not load the card catalog snapshot");
        }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<UniquesSearchClient>();
            var cards = await client.FetchNonUniqueCatalogAsync(ct);
            if (cards.Count == 0) throw new InvalidDataException("uniques-search-api returned an empty non-unique catalog.");

            var now = clock.GetUtcNow();
            catalog.Replace(cards, now);

            var db = scope.ServiceProvider.GetRequiredService<DeckDbContext>();
            var payload = JsonSerializer.Serialize(cards, Json);
            var existing = await db.CardCatalogSnapshots.FirstOrDefaultAsync(ct);
            if (existing is null) db.CardCatalogSnapshots.Add(new CardCatalogSnapshot { Payload = payload, FetchedAt = now });
            else
            {
                existing.Payload = payload;
                existing.FetchedAt = now;
            }
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Card catalog refresh failed (catalog loaded: {Loaded})", catalog.IsLoaded);
            return false;
        }
    }
}
