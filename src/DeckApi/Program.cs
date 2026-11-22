using DeckApi.Admin;
using DeckApi.Auth;
using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Decks;
using DeckApi.Formats;
using DeckApi.Import;
using DeckApi.Legacy;
using DeckApi.Tournaments;
using DeckApi.Validation;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var isImport = args.Length > 0 && args[0] == "import-legacy";

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<DeckDbContext>(o => o
    .UseNpgsql(config.GetConnectionString("decks") ?? throw new InvalidOperationException("ConnectionStrings:decks is required."))
    .UseSnakeCaseNamingConvention());

// Card reference: uniques-search-api + local unique range tables.
builder.Services.AddSingleton(UniqueFactionRanges.LoadEmbedded());
builder.Services.AddSingleton<CardCatalog>();
builder.Services.AddSingleton<ICardCatalog>(sp => sp.GetRequiredService<CardCatalog>());
builder.Services.AddSingleton<CardCatalogRefresher>();
if (!isImport) builder.Services.AddHostedService(sp => sp.GetRequiredService<CardCatalogRefresher>());
builder.Services.AddHttpClient<UniquesSearchClient>(c =>
{
    c.BaseAddress = new Uri(config["UniquesSearch:BaseUrl"] ?? throw new InvalidOperationException("UniquesSearch:BaseUrl is required."));
    c.Timeout = TimeSpan.FromSeconds(config.GetValue("UniquesSearch:TimeoutSeconds", 20));
});

// Formats & validation.
builder.Services.AddSingleton(sp => FormatCatalog.Load(config["Formats:ConfigPath"], sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IFormatCatalog>(sp => sp.GetRequiredService<FormatCatalog>());
builder.Services.AddSingleton(ValidationMessages.LoadEmbedded());
builder.Services.AddScoped<DeckValidationService>();
builder.Services.AddScoped<DeckRevalidator>();
// Season picked on the website's admin page (formats manifest id, e.g. frontier_s05): see FormatSeasonSelector.
builder.Services.AddHttpClient<FormatsManifestClient>(c => c.Timeout = TimeSpan.FromSeconds(config.GetValue("Formats:ManifestTimeoutSeconds", 15)));
builder.Services.AddScoped<FormatSeasonSelector>();
builder.Services.AddSingleton<RevalidationQueue>();
builder.Services.AddSingleton<FormatSeasonSelectionsLoader>();
if (!isImport)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FormatSeasonSelectionsLoader>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RevalidationQueue>());
}
builder.Services.AddHttpClient<AlteredDraftSealedPoolClient>(c =>
{
    // Optional: missing URL = sealed pools unavailable (sealed decks fail closed), never a startup failure.
    if (config["AlteredDraft:BaseUrl"] is { Length: > 0 } url) c.BaseAddress = new Uri(url);
    c.Timeout = TimeSpan.FromSeconds(5);
});

// Old PHP decks-api (write-through + pull). Optional: empty BaseUrl disables the synchronisation.
builder.Services.AddHttpClient<LegacyDecksClient>(c =>
{
    if (config["Legacy:BaseUrl"] is { Length: > 0 } url) c.BaseAddress = new Uri(url);
    c.Timeout = TimeSpan.FromMilliseconds(config.GetValue("Legacy:TimeoutMs", 2000));
});
builder.Services.AddScoped<LegacySyncService>();

// GameApi tournament decks (public, read-only copies). Optional: empty BaseUrl disables the import.
builder.Services.AddHttpClient<GameApiClient>(c =>
{
    if (config["GameApi:BaseUrl"] is { Length: > 0 } url) c.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
    c.Timeout = TimeSpan.FromSeconds(config.GetValue("GameApi:TimeoutSeconds", 20));
});
builder.Services.AddScoped<TournamentDeckSync>();
if (!isImport && config["GameApi:BaseUrl"] is { Length: > 0 } && config.GetValue("GameApi:SyncMinutes", 10) > 0)
    builder.Services.AddHostedService<TournamentDeckSyncWorker>();

builder.Services.AddScoped<DeckService>();
builder.Services.AddScoped<DeckPresenter>();
builder.Services.AddScoped<LegacyImporter>();

builder.Services.AddDeckApiAuth(config, builder.Environment);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => CorsOrigins.IsAllowed(origin, config["Cors:AllowOriginRegex"]))
    .WithMethods("GET", "OPTIONS", "POST", "PUT", "PATCH", "DELETE")
    .WithHeaders("Content-Type", "Authorization")
    .WithExposedHeaders("Link")
    .SetPreflightMaxAge(TimeSpan.FromHours(1))));

var app = builder.Build();

if (isImport)
{
    return await ImportCommand.RunAsync(app, args[1..]);
}

if (config.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DeckDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", (ICardCatalog catalog) => Results.Json(new { status = "ok", cardCatalogLoaded = catalog.IsLoaded, cardCatalogLoadedAt = catalog.LoadedAt }));
app.MapPublicDeckEndpoints();
app.MapDeckEndpoints();
app.MapFormatEndpoints();
app.MapAdminEndpoints();

await app.RunAsync();
return 0;

public partial class Program;

internal static class CorsOrigins
{
    /// <summary>Same semantics as the old API's <c>CORS_ALLOW_ORIGIN</c> regex. Empty = any origin.</summary>
    public static bool IsAllowed(string origin, string? regex) =>
        string.IsNullOrEmpty(regex) || System.Text.RegularExpressions.Regex.IsMatch(origin, regex);
}
