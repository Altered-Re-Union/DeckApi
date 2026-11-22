using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DeckApi.Tournaments;

/// <summary>
/// GameApi's <c>GET /api/tournament-decks</c>: one item per tournament player, every distinct list they played
/// with its record. Keyset-paginated with an opaque cursor; polling with the last <c>next</c> returns only the
/// rows GameApi recomputed since.
/// </summary>
public sealed class GameApiClient(HttpClient http, IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => http.BaseAddress is not null;

    public async Task<TournamentDecksPage> FetchAsync(string? after, int pageSize, CancellationToken ct)
    {
        var url = $"api/tournament-decks?pageSize={pageSize}" + (after is null ? "" : "&after=" + Uri.EscapeDataString(after));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["GameApi:ApiKey"]);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TournamentDecksPage>(Json, ct)
               ?? throw new InvalidDataException("GameApi returned an empty tournament-decks page.");
    }
}

public sealed record TournamentDecksPage(IReadOnlyList<TournamentPlayerDecks> Players, string? Next, bool HasMore);

public sealed record TournamentPlayerDecks(
    long TournamentParentId,
    string? TournamentName,
    string? Mode,
    string BgaUserId,
    string? ReunionUserId,
    string? Pseudo,
    IReadOnlyList<TournamentDeckUsage> Decks,
    DateTimeOffset ComputedAt);

public sealed record TournamentDeckUsage(string Deck, int Games, int Wins, int Losses, DateTimeOffset? LastPlayedAt);
