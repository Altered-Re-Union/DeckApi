using System.Net.Http.Json;
using System.Text.Json;

namespace DeckApi.Cards;

/// <summary>
/// Client for uniques-search-api (Rust). There is no dedicated batch endpoint: lookups go through
/// <c>POST /api/v2/search</c> with a form body (<c>ref[]=...</c>, <c>kind</c>, <c>limit</c> ≤ 200, <c>page</c>).
/// Unknown refs are silently dropped by the API, callers diff the result against their request.
/// </summary>
public sealed class UniquesSearchClient(HttpClient http, ILogger<UniquesSearchClient> logger)
{
    public const int PageSize = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record SearchResponse(SearchIter? Iter, List<CatalogCard>? Cards);
    private sealed record SearchIter(long Total, int? Page, string? Cursor);

    /// <summary>Downloads the whole non-unique catalog (~3.5k prints: commons, rares, exalted, heroes, tokens).</summary>
    public async Task<List<CatalogCard>> FetchNonUniqueCatalogAsync(CancellationToken ct)
    {
        var all = new List<CatalogCard>();
        for (var page = 1; page < 1000; page++)
        {
            var response = await SearchAsync([
                new("kind", "nonunique"),
                new("limit", PageSize.ToString()),
                new("page", page.ToString()),
            ], ct);
            var cards = response.Cards ?? [];
            all.AddRange(cards);
            if (cards.Count < PageSize || (response.Iter is { } iter && all.Count >= iter.Total)) break;
        }
        logger.LogInformation("Fetched {Count} non-unique prints from uniques-search-api", all.Count);
        return all;
    }

    /// <summary>Looks up cards (uniques or not) by reference, 200 per request.</summary>
    public async Task<List<CatalogCard>> FetchByReferencesAsync(IReadOnlyCollection<string> references, CancellationToken ct)
    {
        var result = new List<CatalogCard>();
        foreach (var chunk in references.Distinct().Chunk(PageSize))
        {
            var form = chunk.Select(r => new KeyValuePair<string, string>("ref[]", r)).ToList();
            form.Add(new("limit", PageSize.ToString()));
            var response = await SearchAsync(form, ct);
            result.AddRange(response.Cards ?? []);
        }
        return result;
    }

    private async Task<SearchResponse> SearchAsync(List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync("/api/v2/search", content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SearchResponse>(Json, ct)
               ?? throw new InvalidDataException("Empty search response from uniques-search-api.");
    }
}
