using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeckApi.Formats;

/// <summary>One entry of the formats manifest, e.g. <c>{ "id": "frontier_s05", "path": "frontier.s05.json", "version": 2 }</c>.</summary>
public sealed record FormatsManifestEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("version")] int? Version);

/// <summary>
/// Reads the formats bucket that uniques-search-api and the website read (<c>Formats:ManifestUrl</c>, the prod bucket
/// by default). Each entry's <c>path</c> is relative to the manifest, like uniques-search-api resolves it.
/// </summary>
public sealed class FormatsManifestClient(HttpClient http, IConfiguration config)
{
    public const string DefaultManifestUrl = "https://altered-reunion-formats-prod.s3.fr-par.scw.cloud/manifest.json";

    private Uri ManifestUrl => new(config["Formats:ManifestUrl"] is { Length: > 0 } url ? url : DefaultManifestUrl);

    public async Task<IReadOnlyList<FormatsManifestEntry>> GetManifestAsync(CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(ManifestUrl, ct);
        return await JsonSerializer.DeserializeAsync<List<FormatsManifestEntry>>(stream, FormatCatalog.JsonOptions, ct) ?? [];
    }

    public async Task<HashSet<string>> GetAllowlistAsync(FormatsManifestEntry entry, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(new Uri(ManifestUrl, entry.Path), ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return FormatCatalog.LoadAllowlist(buffer);
    }
}
