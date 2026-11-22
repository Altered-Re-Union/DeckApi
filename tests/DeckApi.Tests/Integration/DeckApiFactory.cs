using System.Net.Http.Headers;
using DeckApi.Auth;
using DeckApi.Cards;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace DeckApi.Tests.Integration;

/// <summary>Real Postgres (Testcontainers), fake old API, synthetic card catalog, dev-signed tokens.</summary>
public sealed class DeckApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string DevKey = "integration-tests-signing-key";
    public const string MaintenanceKey = "integration-maintenance-key";
    public const string BgaKey = "integration-bga-key";

    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public FakeLegacyApi Legacy { get; } = new();
    public FakeGameApi GameApi { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.StartAsync();
        await Legacy.StartAsync();
        await GameApi.StartAsync();
        _ = Services; // boot the app (runs migrations)
        Services.GetRequiredService<CardCatalog>().Replace(TestCards.Catalog(), DateTimeOffset.UtcNow);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:decks", Postgres.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Keycloak:BaseUrl", "");
        builder.UseSetting("Auth:DevSigningKey", DevKey);
        builder.UseSetting("UniquesSearch:BaseUrl", "http://127.0.0.1:9");
        builder.UseSetting("UniquesSearch:TimeoutSeconds", "1");
        builder.UseSetting("AlteredDraft:BaseUrl", "");
        builder.UseSetting("Legacy:BaseUrl", Legacy.BaseUrl);
        builder.UseSetting("ApiKeys:Maintenance", MaintenanceKey);
        builder.UseSetting("ApiKeys:Bga", BgaKey);
        builder.UseSetting("GameApi:BaseUrl", GameApi.BaseUrl);
        builder.UseSetting("GameApi:ApiKey", FakeGameApi.ApiKey);
        builder.UseSetting("GameApi:SyncMinutes", "0"); // synced on demand through the admin endpoint
        builder.UseSetting("GameApi:PageSize", "1"); // exercises pagination
    }

    public HttpClient ClientFor(string sub, string? pseudo = null, string scope = "openid read-decks write-deck")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AuthSetup.CreateDevToken(DevKey, sub, pseudo ?? sub, scope: scope));
        return client;
    }

    public static string BearerOf(HttpClient client) => "Bearer " + client.DefaultRequestHeaders.Authorization!.Parameter;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await Legacy.DisposeAsync();
        await GameApi.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<DeckApiFactory>
{
    public const string Name = "integration";
}
