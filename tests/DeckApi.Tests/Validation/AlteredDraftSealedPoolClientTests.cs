using System.Net;
using System.Text;
using DeckApi.Validation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckApi.Tests.Validation;

public class AlteredDraftSealedPoolClientTests
{
    /// <summary>altered-draft stand-in: answers the pool of the deck ids it knows, 404 otherwise (or <see cref="Status"/>).</summary>
    private sealed class FakeAlteredDraft : HttpMessageHandler
    {
        public Dictionary<Guid, string> Pools { get; } = [];
        public HttpStatusCode? Status { get; set; }
        public List<Guid> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var id = Guid.Parse(request.RequestUri!.Query.Split("deckId=")[1]);
            Asked.Add(id);
            if (Status is { } status) return Task.FromResult(new HttpResponseMessage(status));
            return Task.FromResult(Pools.TryGetValue(id, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (AlteredDraftSealedPoolClient Client, FakeAlteredDraft Draft) Create()
    {
        var draft = new FakeAlteredDraft();
        var http = new HttpClient(draft) { BaseAddress = new Uri("https://draft.test") };
        return (new AlteredDraftSealedPoolClient(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<AlteredDraftSealedPoolClient>.Instance), draft);
    }

    private const string Pool = """{"cards":{"ALT_CORE_B_AX_01_C":1}}""";

    [Fact]
    public async Task A_deck_not_linked_yet_is_found_once_altered_draft_links_it()
    {
        var (client, draft) = Create();
        var deckId = Guid.NewGuid();

        // altered-draft creates the deck first (DeckApi validates it: no pool yet)...
        Assert.Null(await client.GetPoolCountsAsync(deckId, "sub", "token", CancellationToken.None));

        // ...then links it and saves the deck again: the 404 was not cached.
        draft.Pools[deckId] = Pool;
        var pool = await client.GetPoolCountsAsync(deckId, "sub", "token", CancellationToken.None);
        Assert.Equal(1, pool!["ALT_CORE_B_AX_01_C"]);

        // A found pool is cached.
        await client.GetPoolCountsAsync(deckId, "sub", "token", CancellationToken.None);
        Assert.Equal(2, draft.Asked.Count);
    }

    [Fact]
    public async Task Errors_are_cached_briefly()
    {
        var (client, draft) = Create();
        var deckId = Guid.NewGuid();
        draft.Status = HttpStatusCode.BadGateway;

        Assert.Null(await client.GetPoolCountsAsync(deckId, "sub", "token", CancellationToken.None));
        draft.Status = null;
        draft.Pools[deckId] = Pool;
        Assert.Null(await client.GetPoolCountsAsync(deckId, "sub", "token", CancellationToken.None));
        Assert.Single(draft.Asked);
    }

    [Fact]
    public async Task The_deck_id_comes_first_and_the_old_api_id_is_a_fallback()
    {
        var (client, draft) = Create();
        var deckId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();

        // Imported deck whose id was taken: altered-draft only knows the old-API id.
        draft.Pools[legacyId] = Pool;
        Assert.NotNull(await client.GetPoolCountsAsync([deckId, legacyId], "sub", "token", CancellationToken.None));
        Assert.Equal([deckId, legacyId], draft.Asked);

        // Deck created on DeckApi, recreated on the old API with a new id: the deck id wins.
        draft.Asked.Clear();
        var otherDeck = Guid.NewGuid();
        draft.Pools[otherDeck] = Pool;
        Assert.NotNull(await client.GetPoolCountsAsync([otherDeck, Guid.NewGuid()], "sub", "token", CancellationToken.None));
        Assert.Equal([otherDeck], draft.Asked);
    }
}
