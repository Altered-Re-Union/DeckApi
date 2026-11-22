using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DeckApi.Tests.Integration;

/// <summary>
/// Stand-in for GameApi's <c>GET /api/tournament-decks</c>: an append-only log of player rows (a recomputed row
/// is appended again, like GameApi moving it to the end of its ComputedAt order). The cursor is the log index.
/// </summary>
public sealed class FakeGameApi : IAsyncDisposable
{
    public const string ApiKey = "integration-gameapi-key";

    private readonly WebApplication _app;
    private readonly List<JsonObject> _rows = [];
    public string BaseUrl { get; private set; } = "";

    public FakeGameApi()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet("/api/tournament-decks", (HttpContext ctx) =>
        {
            if (ctx.Request.Headers.Authorization.ToString() != "Bearer " + ApiKey) return Results.Unauthorized();
            var after = int.TryParse(ctx.Request.Query["after"], out var a) ? a : -1;
            var size = int.TryParse(ctx.Request.Query["pageSize"], out var s) ? s : 200;
            List<JsonObject> rows;
            lock (_rows) rows = _rows.Select((r, i) => (r, i)).Where(x => x.i > after).Take(size).Select(x => (JsonObject)x.r.DeepClone()).ToList();
            int last;
            lock (_rows) last = Math.Min(after + size, _rows.Count - 1);
            bool hasMore;
            lock (_rows) hasMore = last < _rows.Count - 1;
            return Results.Json(new JsonObject
            {
                ["players"] = new JsonArray(rows.Cast<JsonNode>().ToArray()),
                ["next"] = rows.Count > 0 ? last.ToString() : (after >= 0 ? after.ToString() : null),
                ["hasMore"] = hasMore,
            });
        });
    }

    public void Publish(long tournamentId, string tournamentName, string mode, string bgaUserId, string? reunionUserId, string? pseudo,
        params (string Deck, int Games, int Wins, int Losses, DateTimeOffset LastPlayedAt)[] decks)
    {
        var row = new JsonObject
        {
            ["tournamentParentId"] = tournamentId,
            ["tournamentName"] = tournamentName,
            ["mode"] = mode,
            ["bgaUserId"] = bgaUserId,
            ["reunionUserId"] = reunionUserId,
            ["pseudo"] = pseudo,
            ["decks"] = new JsonArray(decks.Select(d => (JsonNode)new JsonObject
            {
                ["deck"] = d.Deck, ["games"] = d.Games, ["wins"] = d.Wins, ["losses"] = d.Losses, ["lastPlayedAt"] = d.LastPlayedAt.ToString("O"),
            }).ToArray()),
            ["computedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        lock (_rows) _rows.Add(row);
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
