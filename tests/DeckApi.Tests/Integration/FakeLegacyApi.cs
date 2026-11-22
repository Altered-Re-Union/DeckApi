using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DeckApi.Tests.Integration;

/// <summary>
/// In-memory stand-in for the old PHP decks-api (only what the sync uses). Decks are keyed by the bearer
/// token's raw value as "owner", which is enough to isolate users in tests.
/// </summary>
public sealed class FakeLegacyApi : IAsyncDisposable
{
    public sealed class StoredDeck
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public required string Owner { get; init; }
        public JsonObject Body { get; set; } = new();
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow.AddSeconds(-1);
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private readonly WebApplication _app;
    public ConcurrentDictionary<Guid, StoredDeck> Decks { get; } = new();
    public bool Down { get; set; }
    public string BaseUrl { get; private set; } = "";
    public List<string> Calls { get; } = [];

    public FakeLegacyApi()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            lock (Calls) Calls.Add($"{ctx.Request.Method} {ctx.Request.Path}");
            if (Down) { ctx.Response.StatusCode = 503; return; }
            await next();
        });

        _app.MapGet("/api/decks", (HttpContext ctx) =>
            Results.Json(new JsonArray(Decks.Values.Where(d => d.Owner == Owner(ctx)).Select(d => (JsonNode)Render(d, false)).ToArray())));
        _app.MapGet("/api/decks/{id:guid}", (Guid id, HttpContext ctx) =>
            Decks.TryGetValue(id, out var d) && d.Owner == Owner(ctx) ? Results.Json(Render(d, true)) : Results.NotFound());
        _app.MapPost("/api/decks", async (HttpContext ctx) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(ctx.Request.Body))!;
            var deck = new StoredDeck { Owner = Owner(ctx), Body = body, CreatedAt = DateTimeOffset.UtcNow };
            Decks[deck.Id] = deck;
            return Results.Json(Render(deck, false), statusCode: 201);
        });
        _app.MapPatch("/api/decks/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            if (!Decks.TryGetValue(id, out var deck) || deck.Owner != Owner(ctx)) return Results.NotFound();
            var body = (JsonObject)(await JsonNode.ParseAsync(ctx.Request.Body))!;
            foreach (var (k, v) in body.ToList()) deck.Body[k] = v?.DeepClone();
            deck.UpdatedAt = DateTimeOffset.UtcNow;
            return Results.Json(Render(deck, false));
        });
        _app.MapDelete("/api/decks/{id:guid}", (Guid id, HttpContext ctx) =>
            Decks.TryRemove(id, out _) ? Results.NoContent() : Results.NotFound());
    }

    public StoredDeck Seed(string owner, string name, params (string Ref, int Qty)[] cards)
    {
        var deck = new StoredDeck
        {
            Owner = owner,
            Body = new JsonObject
            {
                ["name"] = name, ["description"] = null, ["format"] = "standard", ["isPublic"] = false, ["isDraft"] = false, ["alteredId"] = null,
                ["deckCards"] = new JsonArray(cards.Select(c => (JsonNode)new JsonObject { ["cardReference"] = c.Ref, ["quantity"] = c.Qty }).ToArray()),
            },
        };
        Decks[deck.Id] = deck;
        return deck;
    }

    private static string Owner(HttpContext ctx) => ctx.Request.Headers.Authorization.ToString();

    private static JsonObject Render(StoredDeck d, bool withCards)
    {
        var json = new JsonObject
        {
            ["id"] = d.Id.ToString(),
            ["name"] = d.Body["name"]?.DeepClone(),
            ["description"] = d.Body["description"]?.DeepClone(),
            ["format"] = d.Body["format"]?.DeepClone(),
            ["isPublic"] = d.Body["isPublic"]?.DeepClone() ?? false,
            ["isDraft"] = d.Body["isDraft"]?.DeepClone() ?? false,
            ["createdAt"] = d.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["updatedAt"] = d.UpdatedAt?.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["alteredId"] = d.Body["alteredId"]?.DeepClone(),
        };
        if (withCards)
        {
            var cards = d.Body["deckCards"]?.DeepClone() as JsonArray ?? [];
            if (cards.Count > 0) json["cards"] = cards; else json["deckCards"] = cards;
        }
        return json;
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
