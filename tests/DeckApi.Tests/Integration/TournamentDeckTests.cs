using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DeckApi.Data;
using DeckApi.Decks;
using DeckApi.Deckfmt;

namespace DeckApi.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class TournamentDeckTests(DeckApiFactory factory)
{
    private static async Task<JsonObject> Json(HttpResponseMessage response) =>
        (JsonObject)(await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

    private static string Compress(IEnumerable<DeckLine> lines) =>
        DeckContent.Encode(lines.Select(l => new CardRefQty(l.Quantity, l.Reference)).ToList());

    private async Task<JsonObject> Sync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tournament-decks/sync");
        request.Headers.Add("X-Api-Key", DeckApiFactory.MaintenanceKey);
        var response = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private async Task<List<JsonObject>> PublicDecksOf(string pseudo, string source)
    {
        var page = await Json(await factory.CreateClient().GetAsync($"/api/decks/public?source={source}&itemsPerPage=1000"));
        return page["member"]!.AsArray().Select(m => m!.AsObject()).Where(m => m["owner"]?["pseudo"]?.GetValue<string>() == pseudo).ToList();
    }

    [Fact]
    public async Task Tournament_decks_are_imported_canonical_public_and_attributed_to_the_reunion_pseudo()
    {
        // Played with an alt-art print: stored canonical, like any deck.
        var played = TestCards.StandardAxDeck();
        played[1] = new DeckLine("ALT_CORE_A_AX_04_C", 3);
        var s4 = new DateTimeOffset(2026, 11, 15, 20, 0, 0, TimeSpan.Zero);
        var s3 = new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

        factory.GameApi.Publish(9001, "Winter Cup", "FRONTIER", "bga-1", "kc-tourney-1", "TourneyPlayer", (Compress(played), 4, 3, 1, s4));
        factory.GameApi.Publish(9001, "Winter Cup", "FRONTIER", "bga-2", null, null, (Compress(TestCards.StandardAxDeck()), 4, 1, 3, s4));
        factory.GameApi.Publish(9002, "Autumn Cup", "FRONTIER", "bga-1", "kc-tourney-1", "TourneyPlayer", (Compress(TestCards.StandardAxDeck()), 2, 2, 0, s3));

        var report = await Sync();
        Assert.True(report["completed"]!.GetValue<bool>());
        Assert.True(report["skippedNoReunionAccount"]!.GetValue<int>() >= 1);

        // One deck for the same canonical list across both tournaments.
        var deck = Assert.Single(await PublicDecksOf("TourneyPlayer", "tournament"));
        Assert.Equal("tournament", deck["source"]!.GetValue<string>());
        Assert.Equal("frontier", deck["format"]!.GetValue<string>());
        Assert.True(deck["legal"]!.GetValue<bool>());
        Assert.Equal(Compress(DeckCanonicalizer.Canonicalize(played, null, null, factory.Services.GetCatalog()).Lines), deck["compressed"]!.GetValue<string>());
        Assert.Equal(["Winter Cup", "Autumn Cup"], deck["tournaments"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.Equal((3, 1), (deck["tournaments"]![0]!["wins"]!.GetValue<int>(), deck["tournaments"]![0]!["losses"]!.GetValue<int>()));
        Assert.Equal(["S4", "S3"], deck["validSeasons"]!.AsArray().Select(s => s!["season"]!.GetValue<string>()).Order().Reverse());
        Assert.DoesNotContain("bga", deck.ToJsonString());

        // Not in the default (old) listing, in the unified one.
        Assert.Empty(await PublicDecksOf("TourneyPlayer", "user"));
        Assert.Single(await PublicDecksOf("TourneyPlayer", "all"));

        // Independent of the player's own decks: not in their listing (nor BGA's), read-only.
        var owner = factory.ClientFor("kc-tourney-1", "TourneyPlayer");
        var mine = (JsonArray)(await JsonNode.ParseAsync(await (await owner.GetAsync("/api/decks")).Content.ReadAsStreamAsync()))!;
        Assert.DoesNotContain(mine, d => d!["id"]!.GetValue<string>() == deck["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PatchAsJsonAsync($"/api/decks/{deck["id"]}", new { name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"/api/decks/{deck["id"]}")).StatusCode);

        // GameApi recomputes the Autumn Cup row with another list: the old result is replaced.
        var other = TestCards.StandardAxDeck();
        other[^1] = new DeckLine("ALT_CORE_B_AX_17_C", 3);
        factory.GameApi.Publish(9002, "Autumn Cup", "FRONTIER", "bga-1", "kc-tourney-1", "TourneyPlayer", (Compress(other), 3, 2, 1, s3));
        await Sync();

        var decks = await PublicDecksOf("TourneyPlayer", "tournament");
        Assert.Equal(2, decks.Count);
        var first = decks.Single(d => d["id"]!.GetValue<string>() == deck["id"]!.GetValue<string>());
        Assert.Equal(["Winter Cup"], first["tournaments"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        var second = decks.Single(d => d["id"]!.GetValue<string>() != deck["id"]!.GetValue<string>());
        Assert.Equal(3, second["tournaments"]![0]!["games"]!.GetValue<int>());

        // Searchable with the usual filters.
        var bySeason = await Json(await factory.CreateClient().GetAsync("/api/decks/public?source=tournament&season=frontier:S3&cardReference=ALT_CORE_B_AX_17_C"));
        Assert.Contains(bySeason["member"]!.AsArray(), m => m!["id"]!.GetValue<string>() == second["id"]!.GetValue<string>());
    }
}

internal static class ServiceProviderExtensions
{
    public static DeckApi.Cards.ICardCatalog GetCatalog(this IServiceProvider services) =>
        (DeckApi.Cards.ICardCatalog)services.GetService(typeof(DeckApi.Cards.ICardCatalog))!;
}
