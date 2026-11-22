using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeckApi.Data;

namespace DeckApi.Legacy;

/// <summary>Deck as returned by the old PHP decks-api (<c>deck:read</c>, plus cards on item GET).</summary>
public sealed class LegacyDeck
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Format { get; init; }
    public bool IsPublic { get; init; }
    public bool IsDraft { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? AlteredId { get; init; }

    /// <summary>Item GET returns <c>cards</c> when the deck has cards, <c>deckCards</c> when it is empty.</summary>
    public List<LegacyCardLine>? Cards { get; init; }
    public List<LegacyCardLine>? DeckCards { get; init; }

    [JsonIgnore] public DateTimeOffset Stamp => UpdatedAt ?? CreatedAt;

    public List<DeckLine> Lines() =>
        (Cards ?? DeckCards ?? []).Select(c => new DeckLine(c.CardReference, c.Quantity)).ToList();
}

public sealed record LegacyCardLine(string CardReference, int Quantity);

/// <summary>Body sent on POST/PATCH (old API <c>deck:write</c> group).</summary>
public sealed record LegacyDeckWrite(
    string Name,
    string? Description,
    string? Format,
    bool IsPublic,
    bool IsDraft,
    IReadOnlyList<LegacyCardLine> DeckCards,
    string? AlteredId);

public enum LegacyCallStatus { Ok, NotFound, Failed }

public sealed record LegacyResult<T>(LegacyCallStatus Status, T? Value = default)
{
    public bool Ok => Status == LegacyCallStatus.Ok;
}

/// <summary>
/// Old API client. Every call forwards the caller's own Keycloak bearer (same realm on both APIs) and is
/// bounded by <c>Legacy:TimeoutMs</c> (default 2 s): the old API is never a hard dependency, a failure is
/// reported as <see cref="LegacyCallStatus.Failed"/> and the caller degrades.
/// </summary>
public sealed class LegacyDecksClient(HttpClient http, ILogger<LegacyDecksClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public bool Enabled => http.BaseAddress is not null;

    public Task<LegacyResult<List<LegacyDeck>>> ListMineAsync(string token, CancellationToken ct) =>
        SendAsync<List<LegacyDeck>>(HttpMethod.Get, "/api/decks", token, null, ct);

    public Task<LegacyResult<LegacyDeck>> GetAsync(Guid id, string token, CancellationToken ct) =>
        SendAsync<LegacyDeck>(HttpMethod.Get, $"/api/decks/{id}", token, null, ct);

    public Task<LegacyResult<LegacyDeck>> CreateAsync(LegacyDeckWrite body, string token, CancellationToken ct) =>
        SendAsync<LegacyDeck>(HttpMethod.Post, "/api/decks", token, JsonContent.Create(body, options: Json), ct);

    public Task<LegacyResult<LegacyDeck>> UpdateAsync(Guid id, LegacyDeckWrite body, string token, CancellationToken ct)
    {
        var content = JsonContent.Create(body, new MediaTypeHeaderValue("application/merge-patch+json"), Json);
        return SendAsync<LegacyDeck>(HttpMethod.Patch, $"/api/decks/{id}", token, content, ct);
    }

    public async Task<LegacyCallStatus> DeleteAsync(Guid id, string token, CancellationToken ct) =>
        (await SendAsync<object>(HttpMethod.Delete, $"/api/decks/{id}", token, null, ct)).Status;

    private async Task<LegacyResult<T>> SendAsync<T>(HttpMethod method, string path, string token, HttpContent? content, CancellationToken ct)
    {
        if (!Enabled) return new(LegacyCallStatus.Failed);
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(LegacyCallStatus.NotFound);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("legacy {Method} {Path} returned {Status}: {Body}", method, path, (int)response.StatusCode,
                    body.Length > 500 ? body[..500] : body);
                return new(LegacyCallStatus.Failed);
            }
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return new(LegacyCallStatus.Ok);
            return new(LegacyCallStatus.Ok, await response.Content.ReadFromJsonAsync<T>(Json, ct));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning("legacy {Method} {Path} failed: {Error}", method, path, e.Message);
            return new(LegacyCallStatus.Failed);
        }
    }
}
