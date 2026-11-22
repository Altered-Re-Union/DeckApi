using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace DeckApi.Validation;

/// <summary>
/// Sealed pool membership, ported from the PHP <c>AlteredDraftSealedPoolClient</c>:
/// <c>GET {AlteredDraft:BaseUrl}/api/tournament-pool-by-deck?deckId=...</c> with the caller's own bearer
/// (altered-draft resolves the Keycloak sub itself, so a caller only ever sees their own pool).
///
/// altered-draft links each pool to the id of the deck it created. It creates them on DeckApi, so that is
/// <c>Deck.Id</c>; decks it created on the old decks-api were imported with the same id. The old-API id is only
/// tried as a fallback (a deck whose id was taken at import), never first: a deck recreated on the old API gets
/// a new id there that altered-draft does not know.
///
/// Cached per (sub, deckId): 1 h on success, 30 s on an error. A 404 (no pool linked to that deck) is not cached:
/// altered-draft creates the deck first and links it right after, and the next write must see the link.
/// Returns null when no pool is found: the sealed validator then fails closed.
/// </summary>
public sealed class AlteredDraftSealedPoolClient(HttpClient http, IMemoryCache cache, ILogger<AlteredDraftSealedPoolClient> logger)
{
    private sealed record PoolResponse(Dictionary<string, int>? Cards);

    /// <summary>The pool of the first of <paramref name="deckIds"/> altered-draft knows (distinct ids, in order).</summary>
    public async Task<IReadOnlyDictionary<string, int>?> GetPoolCountsAsync(IEnumerable<Guid> deckIds, string? sub, string? bearerToken, CancellationToken ct)
    {
        foreach (var deckId in deckIds.Distinct())
            if (await GetPoolCountsAsync(deckId, sub, bearerToken, ct) is { } pool) return pool;
        return null;
    }

    public async Task<IReadOnlyDictionary<string, int>?> GetPoolCountsAsync(Guid deckId, string? sub, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(bearerToken) || http.BaseAddress is null) return null;

        var key = $"sealed-pool:{sub}:{deckId}";
        if (cache.TryGetValue(key, out IReadOnlyDictionary<string, int>? cached)) return cached;

        IReadOnlyDictionary<string, int>? pool = null;
        var cacheable = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tournament-pool-by-deck?deckId={deckId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                pool = (await response.Content.ReadFromJsonAsync<PoolResponse>(ct))?.Cards;
            else
            {
                cacheable = response.StatusCode != HttpStatusCode.NotFound;
                logger.LogInformation("altered-draft pool lookup for deck {DeckId} returned {Status}", deckId, (int)response.StatusCode);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning(e, "altered-draft pool lookup failed for deck {DeckId}", deckId);
        }

        if (cacheable) cache.Set(key, pool, pool is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromHours(1));
        return pool;
    }
}
