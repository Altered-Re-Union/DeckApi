using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DeckApi.Data;

namespace DeckApi.Tests.Integration;

/// <summary>The website's admin page switches the Frontier season: DeckApi follows and revalidates Frontier decks.</summary>
[Collection(SeasonSwitchCollection.Name)]
public sealed class SeasonSwitchTests(DeckApiFactory factory)
{
    private const string S5Unique = "ALT_CORE_B_AX_04_U_1"; // not in the embedded S4 allowlist

    private static async Task<JsonObject> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())!;

    private HttpRequestMessage Maintenance(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", DeckApiFactory.MaintenanceKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task Selecting_a_season_on_the_website_makes_it_current_and_revalidates_frontier_decks()
    {
        var owner = factory.ClientFor("user-season-switch");
        var lines = TestCards.StandardAxDeck();
        lines[1] = lines[1] with { Quantity = 2 };
        lines.Add(new DeckLine(S5Unique, 1));
        var created = await Json(await owner.PostAsJsonAsync("/api/decks", new { name = "S5 deck", format = "frontier", isPublic = false, deckCards = lines.Select(l => new { cardReference = l.Reference, quantity = l.Quantity }) }));
        Assert.False(created["legal"]!.GetValue<bool>()); // S4: Unique not allowed

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/formats/season", new { formatId = "frontier_s05" })).StatusCode);

        // The site's default "frontier" id is not a season; a season missing from the manifest is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.SendAsync(Maintenance(HttpMethod.Post, "/api/admin/formats/season", new { formatId = "frontier" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await anonymous.SendAsync(Maintenance(HttpMethod.Post, "/api/admin/formats/season", new { formatId = "frontier_s09" }))).StatusCode);

        factory.FormatsBucket.Publish("frontier_s05", "frontier.s05.json", 2, S5Unique);
        var response = await anonymous.SendAsync(Maintenance(HttpMethod.Post, "/api/admin/formats/season", new { formatId = "frontier_s05" }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var selected = await Json(response);
        Assert.Equal(("frontier", "S5", 1), (selected["format"]!.GetValue<string>(), selected["season"]!.GetValue<string>(), selected["uniqueAllowlistCount"]!.GetValue<int>()));

        // Background revalidation.
        JsonObject status;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            await Task.Delay(100);
            status = await Json(await anonymous.SendAsync(Maintenance(HttpMethod.Get, "/api/admin/formats/frontier/season")));
        } while (status["lastRevalidation"] is null && DateTime.UtcNow < deadline);
        Assert.Equal(("frontier_s05", 2), (status["selection"]!["manifestId"]!.GetValue<string>(), status["selection"]!["manifestVersion"]!.GetValue<int>()));
        Assert.Null(status["lastRevalidation"]!["error"]);
        Assert.True(status["lastRevalidation"]!["changed"]!.GetValue<int>() >= 1);

        var deck = await Json(await owner.GetAsync($"/api/decks/{created["id"]}"));
        Assert.True(deck["legal"]!.GetValue<bool>());
        Assert.Contains(deck["validSeasons"]!.AsArray(), s => s!["season"]!.GetValue<string>() == "S5");

        var formats = (await (await anonymous.GetAsync("/api/formats")).Content.ReadFromJsonAsync<JsonArray>())!;
        var frontier = formats.Single(f => f!["code"]!.GetValue<string>() == "frontier")!;
        Assert.Equal("S5", frontier["season"]!.GetValue<string>());
        Assert.Equal(["S1", "S2", "S3", "S4", "S5"], frontier["seasons"]!.AsArray().Select(s => s!["code"]!.GetValue<string>()));
    }
}
