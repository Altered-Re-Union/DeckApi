using System.Text.Json;
using System.Threading.Channels;
using DeckApi.Data;
using DeckApi.Formats;
using DeckApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace DeckApi.Admin;

public sealed record RevalidationReport(string Format, int Processed, int Changed, int Skipped, int CatalogUnavailable, int SeasonsAdded);

/// <summary>
/// Recomputes legality of every deck of a format, drafts included, in batches. Sealed decks are skipped: their pool
/// can only be read with the owner's own token. A deck legal in the current season gets it in <c>validSeasons</c>;
/// <paramref name="backfill"/> also checks every past season (see <see cref="DeckValidationService.BackfillSeasonsAsync"/>).
/// </summary>
public sealed class DeckRevalidator(DeckDbContext db, DeckValidationService validation, ILogger<DeckRevalidator> logger)
{
    public async Task<RevalidationReport> RunAsync(FormatDefinition format, bool backfill, CancellationToken ct)
    {
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
                var before = (deck.Legal, JsonSerializer.Serialize(deck.ValidationErrors));
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
                        if (before != (deck.Legal, JsonSerializer.Serialize(deck.ValidationErrors))) changed++;
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            after = batch[^1].Id;
        }

        logger.LogInformation("Revalidated {Format} ({Season}): {Processed} processed, {Changed} changed, {Skipped} skipped, {Unavailable} catalog unavailable, {SeasonsAdded} seasons added",
            format.Code, format.Season?.Code, processed, changed, skipped, unavailable, seasonsAdded);
        return new RevalidationReport(format.Code, processed, changed, skipped, unavailable, seasonsAdded);
    }
}

/// <summary>
/// Runs format revalidations in the background, one at a time, so an admin action (e.g. a season switch from the
/// website) answers immediately. A format already waiting is not queued twice.
/// </summary>
public sealed class RevalidationQueue(IServiceScopeFactory scopes, ILogger<RevalidationQueue> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    /// <summary>Last finished revalidation per format, for <c>GET /api/admin/formats/{format}/season</c>.</summary>
    public IReadOnlyDictionary<string, (DateTimeOffset FinishedAt, RevalidationReport? Report, string? Error)> Last => _last;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset, RevalidationReport?, string?)> _last = new();

    public bool IsPending(string format) { lock (_pending) return _pending.Contains(format); }

    public void Enqueue(string format)
    {
        lock (_pending)
        {
            if (!_pending.Add(format)) return;
        }
        _queue.Writer.TryWrite(format);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var code in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_pending) _pending.Remove(code);
            try
            {
                using var scope = scopes.CreateScope();
                var format = scope.ServiceProvider.GetRequiredService<IFormatCatalog>().Find(code);
                if (format is null) continue;
                var report = await scope.ServiceProvider.GetRequiredService<DeckRevalidator>().RunAsync(format, backfill: false, stoppingToken);
                _last[code] = (DateTimeOffset.UtcNow, report, null);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Background revalidation of {Format} failed", code);
                _last[code] = (DateTimeOffset.UtcNow, null, e.Message);
            }
        }
    }
}
