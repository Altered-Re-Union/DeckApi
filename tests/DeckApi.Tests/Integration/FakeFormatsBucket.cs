using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace DeckApi.Tests.Integration;

/// <summary>Stand-in for the formats bucket the website's "Uniques format" page lists: <c>manifest.json</c> + season files.</summary>
public sealed class FakeFormatsBucket : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, (string Path, int Version, string[] Refs)> _entries = new();
    public string BaseUrl { get; private set; } = "";
    public string ManifestUrl => BaseUrl + "/manifest.json";

    public FakeFormatsBucket()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet("/manifest.json", () => Results.Json(new JsonArray(_entries.OrderBy(e => e.Key)
            .Select(e => (JsonNode)new JsonObject { ["id"] = e.Key, ["name"] = e.Key, ["path"] = e.Value.Path, ["version"] = e.Value.Version })
            .ToArray())));
        _app.MapGet("/{file}", (string file) =>
        {
            var entry = _entries.FirstOrDefault(e => e.Value.Path == file);
            return entry.Key is null
                ? Results.NotFound()
                : Results.Json(new JsonObject { ["id"] = entry.Key, ["version"] = entry.Value.Version, ["included_refs"] = new JsonArray(entry.Value.Refs.Select(r => (JsonNode)r).ToArray()) });
        });
    }

    public void Publish(string id, string path, int version, params string[] refs) => _entries[id] = (path, version, refs);

    public async Task StartAsync()
    {
        await _app.StartAsync();
        BaseUrl = _app.Urls.First();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
