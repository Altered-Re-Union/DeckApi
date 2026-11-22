using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DeckApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeckApi.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class DeckApiTests(DeckApiFactory factory)
{
    private static object DeckBody(string name, IEnumerable<DeckLine> lines, string format = "standard", bool isPublic = false, object? altArtOverrides = null) => new
    {
        name,
        format,
        isPublic,
        deckCards = lines.Select(l => new { cardReference = l.Reference, quantity = l.Quantity }),
        altArtOverrides,
    };

    private static async Task<JsonObject> Json(HttpResponseMessage response) =>
        (JsonObject)(await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

    private static async Task<JsonArray> JsonArr(HttpResponseMessage response) =>
        (JsonArray)(await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

    [Fact]
    public async Task Create_returns_old_shape_plus_errors_and_writes_through_to_old_api()
    {
        var client = factory.ClientFor("user-create", "Alice");
        var response = await client.PostAsJsonAsync("/api/decks", DeckBody("Axiom", TestCards.StandardAxDeck()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var deck = await Json(response);

        Assert.Equal(
            ["id", "name", "description", "format", "isPublic", "isDraft", "user", "createdAt", "updatedAt", "stats", "formatErrors", "legal",
             "legalityDetail", "alteredId", "viewCount", "upvoteCount", "errors", "compressed", "altArtOverrides", "source", "tournaments", "validSeasons", "legacyDeletedAt"],
            deck.Select(kv => kv.Key));
        Assert.True(deck["legal"]!.GetValue<bool>());
        Assert.Empty(deck["errors"]!.AsArray());
        Assert.Equal(39, deck["stats"]!["totalCards"]!.GetValue<int>());
        Assert.Equal(TestCards.AxHero, deck["stats"]!["hero"]!["reference"]!.GetValue<string>());
        Assert.True(deck["legalityDetail"]!["global"]!.GetValue<bool>());

        // Written through to the old API with the user's own token.
        var legacy = Assert.Single(factory.Legacy.Decks.Values, d => d.Owner == DeckApiFactory.BearerOf(client));
        Assert.Equal("Axiom", legacy.Body["name"]!.GetValue<string>());
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<DeckDbContext>().Decks.SingleAsync(d => d.Id == Guid.Parse(deck["id"]!.GetValue<string>()));
        Assert.Equal(legacy.Id, stored.LegacyId);
        Assert.False(stored.LegacySyncPending);

        // Item GET: legacy `cards` detail.
        var detail = await Json(await client.GetAsync($"/api/decks/{deck["id"]}?locale=en"));
        Assert.Equal(14, detail["cards"]!.AsArray().Count);
        Assert.Equal("Card AX 4", detail["cards"]!.AsArray().First(c => c!["cardReference"]!.GetValue<string>() == "ALT_CORE_B_AX_04_C")!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Errors_are_listed_with_reference_and_translated()
    {
        var client = factory.ClientFor("user-errors");
        var lines = TestCards.StandardAxDeck();
        lines.Add(new("ALT_CORE_B_BR_06_C", 1));
        lines.Add(new("ALT_CORE_B_AX_04_R1", 1));

        var deck = await Json(await client.PostAsJsonAsync("/api/decks?locale=fr", DeckBody("Invalid", lines)));
        Assert.False(deck["legal"]!.GetValue<bool>());
        var errors = deck["errors"]!.AsArray().Select(e => (Code: e!["code"]!.GetValue<string>(), Ref: e["reference"]?.GetValue<string>(), Message: e["message"]!.GetValue<string>())).ToList();
        Assert.Contains(errors, e => e.Code == "deck.factions.multiple" && e.Ref is null && e.Message.StartsWith("Le deck contient"));
        Assert.Contains(errors, e => e is { Code: "card.suspended", Ref: "ALT_CORE_B_BR_06_C" } && e.Message == "La carte « Suspended card (fr) » est suspendue.");
        Assert.Contains(errors, e => e is { Code: "card.family.copies", Ref: "ALT_CORE_B_AX_04_C" });
        Assert.Equal(errors.Select(e => e.Message), deck["formatErrors"]!.AsArray().Select(m => m!.GetValue<string>()));

        // Same deck, English by default.
        var english = await Json(await client.GetAsync($"/api/decks/{deck["id"]}"));
        Assert.Contains(english["errors"]!.AsArray(), e => e!["message"]!.GetValue<string>() == "Card \"Suspended card\" is suspended.");
    }

    [Fact]
    public async Task Input_violations_use_old_api_shape()
    {
        var client = factory.ClientFor("user-violations");
        var response = await client.PostAsJsonAsync("/api/decks", new { name = "", deckCards = new[] { new { cardReference = "nope", quantity = 1 } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(["name", "deckCards[0].cardReference"], body["violations"]!.AsArray().Select(v => v!["propertyPath"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Public_listing_exposes_owner_pseudo_and_filters_by_card()
    {
        var client = factory.ClientFor("user-public", "PublicPlayer");
        var lines = TestCards.StandardAxDeck();
        lines[1] = lines[1] with { Quantity = 2 };
        lines.Add(new("ALT_CORE_A_AX_28_R1", 1));
        var created = await Json(await client.PostAsJsonAsync("/api/decks", DeckBody("Shared deck", lines, isPublic: true)));

        var anonymous = factory.CreateClient();
        var page = await Json(await anonymous.GetAsync("/api/decks/public?cardReference=ALT_CORE_A_AX_28_R1"));
        var member = Assert.Single(page["member"]!.AsArray());
        Assert.Equal("PublicPlayer", member!["owner"]!["pseudo"]!.GetValue<string>());
        Assert.False(member["hasUpvoted"]!.GetValue<bool>());
        Assert.Equal(1, page["totalItems"]!.GetValue<int>());

        // cardName matches the family, whatever the print.
        var byName = await Json(await anonymous.GetAsync("/api/decks/public?cardName=card ax 28"));
        Assert.Contains(byName["member"]!.AsArray(), m => m!["id"]!.GetValue<string>() == created["id"]!.GetValue<string>());

        // Upvote toggle.
        var voter = factory.ClientFor("user-voter");
        var vote = await Json(await voter.PostAsync($"/api/decks/{created["id"]}/upvote", null));
        Assert.True(vote["hasUpvoted"]!.GetValue<bool>());
        Assert.Equal(1, vote["upvoteCount"]!.GetValue<int>());
        vote = await Json(await voter.PostAsync($"/api/decks/{created["id"]}/upvote", null));
        Assert.Equal(0, vote["upvoteCount"]!.GetValue<int>());

        // Private decks stay private.
        var priv = await Json(await client.PostAsJsonAsync("/api/decks", DeckBody("Private", lines)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/decks/{priv["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await voter.GetAsync($"/api/decks/{priv["id"]}")).StatusCode);

        // altered-bga-api reads any deck by id with its service key (the old BGA item route was public).
        var bga = new HttpRequestMessage(HttpMethod.Get, $"/api/decks/{priv["id"]}");
        bga.Headers.Add("X-Api-Key", DeckApiFactory.BgaKey);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(bga)).StatusCode);
    }

    [Fact]
    public async Task Drafts_are_validated_and_listed_illegal_decks_are_not()
    {
        var client = factory.ClientFor("user-drafts", "DraftPlayer");

        // isDraft only means "hidden in BGA": the deck is still validated and publicly listed.
        var lines = TestCards.StandardAxDeck();
        var draft = await Json(await client.PostAsJsonAsync("/api/decks",
            new { name = "Draft", format = "standard", isPublic = true, isDraft = true,
                  deckCards = lines.Select(l => new { cardReference = l.Reference, quantity = l.Quantity }) }));
        Assert.True(draft["isDraft"]!.GetValue<bool>());
        Assert.True(draft["legal"]!.GetValue<bool>());
        Assert.True(draft["legalityDetail"]!["global"]!.GetValue<bool>());

        // An illegal public deck is not listed.
        var illegalLines = TestCards.StandardAxDeck();
        illegalLines.Add(new("ALT_CORE_B_BR_06_C", 1));
        var illegal = await Json(await client.PostAsJsonAsync("/api/decks", DeckBody("Illegal", illegalLines, isPublic: true)));
        Assert.False(illegal["legal"]!.GetValue<bool>());

        var page = await Json(await factory.CreateClient().GetAsync("/api/decks/public?name=draft"));
        Assert.Contains(page["member"]!.AsArray(), m => m!["id"]!.GetValue<string>() == draft["id"]!.GetValue<string>());
        var all = await Json(await factory.CreateClient().GetAsync("/api/decks/public?itemsPerPage=1000"));
        Assert.DoesNotContain(all["member"]!.AsArray(), m => m!["id"]!.GetValue<string>() == illegal["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Alt_art_overrides_are_optional_validated_and_pruned()
    {
        var client = factory.ClientFor("user-altart");
        var lines = TestCards.StandardAxDeck();
        var overrides = new
        {
            cards = new Dictionary<string, object[]> { ["ALT_CORE_B_AX_04_C"] = [new { reference = "ALT_CORE_A_AX_04_C", quantity = 2 }] },
            tokens = new[] { new { reference = "ALT_CORE_A_BR_31_C", quantity = 1 } },
        };
        var deck = await Json(await client.PostAsJsonAsync("/api/decks", DeckBody("Alt", lines, altArtOverrides: overrides)));
        Assert.Equal("ALT_CORE_A_AX_04_C", deck["altArtOverrides"]!["cards"]!["ALT_CORE_B_AX_04_C"]![0]!["reference"]!.GetValue<string>());
        Assert.True(deck["legal"]!.GetValue<bool>());

        // Another family is refused.
        var bad = new { cards = new Dictionary<string, object[]> { ["ALT_CORE_B_AX_04_C"] = [new { reference = "ALT_CORE_A_AX_05_C", quantity = 1 }] } };
        var refused = await client.PatchAsync($"/api/decks/{deck["id"]}", JsonContent.Create(new { altArtOverrides = bad }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        // An old client replacing the list without the field: overrides for removed cards are dropped, tokens kept.
        var newLines = lines.Where(l => l.Reference != "ALT_CORE_B_AX_04_C").Append(new("ALT_CORE_B_AX_20_C", 3));
        var patched = await Json(await client.PatchAsync($"/api/decks/{deck["id"]}", new StringContent(
            new JsonObject { ["deckCards"] = new JsonArray(newLines.Select(l => (JsonNode)new JsonObject { ["cardReference"] = l.Reference, ["quantity"] = l.Quantity }).ToArray()) }.ToJsonString(),
            Encoding.UTF8, "application/merge-patch+json")));
        Assert.Empty(patched["altArtOverrides"]!["cards"]!.AsObject());
        Assert.Single(patched["altArtOverrides"]!["tokens"]!.AsArray());
    }

    [Fact]
    public async Task Listing_pulls_decks_created_on_the_old_api_and_deletions()
    {
        var client = factory.ClientFor("user-pull");
        var owner = DeckApiFactory.BearerOf(client);
        var seeded = factory.Legacy.Seed(owner, "Made on the old site", TestCards.StandardAxDeck().Select(l => (l.Reference, l.Quantity)).ToArray());

        var list = await JsonArr(await client.GetAsync("/api/decks"));
        var pulled = Assert.Single(list, d => d!["name"]!.GetValue<string>() == "Made on the old site");
        Assert.Equal(seeded.Id.ToString(), pulled!["id"]!.GetValue<string>()); // same id on both APIs
        Assert.True(pulled["legal"]!.GetValue<bool>());
        Assert.Equal(39, pulled["stats"]!["totalCards"]!.GetValue<int>());

        // Modified on the old site → updated here.
        seeded.Body["name"] = "Renamed on the old site";
        seeded.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(5);
        list = await JsonArr(await client.GetAsync("/api/decks"));
        Assert.Contains(list, d => d!["name"]!.GetValue<string>() == "Renamed on the old site");

        // Deleted on the old site → archived here (isDraft: hidden in BGA), never deleted, still listed.
        factory.Legacy.Decks.TryRemove(seeded.Id, out var removed);
        list = await JsonArr(await client.GetAsync("/api/decks"));
        var archived = Assert.Single(list, d => d!["id"]!.GetValue<string>() == seeded.Id.ToString());
        Assert.True(archived!["isDraft"]!.GetValue<bool>());
        Assert.NotNull(archived["legacyDeletedAt"]);
        Assert.Single(await JsonArr(await client.GetAsync("/api/decks?isDraft=true")), d => d!["id"]!.GetValue<string>() == seeded.Id.ToString());

        // Listed again (e.g. it was only missing from a partial listing) → restored with the old API's isDraft.
        factory.Legacy.Decks[seeded.Id] = removed!;
        list = await JsonArr(await client.GetAsync("/api/decks"));
        var restored = Assert.Single(list, d => d!["id"]!.GetValue<string>() == seeded.Id.ToString());
        Assert.False(restored!["isDraft"]!.GetValue<bool>());
        Assert.Null(restored["legacyDeletedAt"]);
    }

    [Fact]
    public async Task Unarchiving_a_deck_deleted_on_the_old_api_recreates_it_there()
    {
        var client = factory.ClientFor("user-unarchive");
        var owner = DeckApiFactory.BearerOf(client);
        var created = await Json(await client.PostAsJsonAsync("/api/decks", DeckBody("Comeback", TestCards.StandardAxDeck(), isPublic: true)));
        var id = created["id"]!.GetValue<string>();
        var legacyDeck = Assert.Single(factory.Legacy.Decks.Values, d => d.Owner == owner);

        factory.Legacy.Decks.TryRemove(legacyDeck.Id, out _);
        var archived = Assert.Single(await JsonArr(await client.GetAsync("/api/decks")));
        Assert.True(archived!["isDraft"]!.GetValue<bool>());

        // An archived deck stays shared.
        var publicList = await Json(await factory.CreateClient().GetAsync("/api/decks/public?name=Comeback"));
        Assert.Contains(id, publicList.ToJsonString());

        // Edits while archived stay local.
        var renamed = await client.PatchAsync($"/api/decks/{id}", new StringContent("""{"name":"Comeback 2"}""", Encoding.UTF8, "application/merge-patch+json"));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.DoesNotContain(factory.Legacy.Decks.Values, d => d.Owner == owner);

        var response = await client.PatchAsync($"/api/decks/{id}", new StringContent("""{"isDraft":false}""", Encoding.UTF8, "application/merge-patch+json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.False(body["isDraft"]!.GetValue<bool>());
        Assert.Null(body["legacyDeletedAt"]);
        var recreated = Assert.Single(factory.Legacy.Decks.Values, d => d.Owner == owner);
        Assert.Equal("Comeback 2", recreated.Body["name"]!.GetValue<string>());

        // The next sync keeps it: it is listed on the old API again.
        var list = await JsonArr(await client.GetAsync("/api/decks"));
        var kept = Assert.Single(list, d => d!["id"]!.GetValue<string>() == id);
        Assert.Null(kept!["legacyDeletedAt"]);
    }

    [Fact]
    public async Task Old_api_down_does_not_block_writes_and_is_caught_up_later()
    {
        var client = factory.ClientFor("user-down");
        var owner = DeckApiFactory.BearerOf(client);
        factory.Legacy.Down = true;
        try
        {
            var response = await client.PostAsJsonAsync("/api/decks", DeckBody("Offline", TestCards.StandardAxDeck()));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.DoesNotContain(factory.Legacy.Decks.Values, d => d.Owner == owner);
            Assert.NotEmpty(await JsonArr(await client.GetAsync("/api/decks"))); // listing still works
        }
        finally
        {
            factory.Legacy.Down = false;
        }

        await client.GetAsync("/api/decks"); // next sync pushes the pending deck
        Assert.Single(factory.Legacy.Decks.Values, d => d.Owner == owner && d.Body["name"]!.GetValue<string>() == "Offline");
    }

    [Fact]
    public async Task Revalidate_requires_the_maintenance_key()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/admin/decks/revalidate?format=standard", null)).StatusCode);

        var client = factory.ClientFor("user-revalidate");
        await client.PostAsJsonAsync("/api/decks", DeckBody("To revalidate", TestCards.StandardAxDeck()));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/decks/revalidate?format=STANDARD");
        request.Headers.Add("X-Api-Key", DeckApiFactory.MaintenanceKey);
        var response = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await Json(response);
        Assert.Equal("standard", report["format"]!.GetValue<string>());
        Assert.True(report["processed"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task Frontier_decks_record_the_seasons_they_were_legal_in()
    {
        var client = factory.ClientFor("user-seasons");
        var created = await Json(await client.PostAsJsonAsync("/api/decks", DeckBody("Frontier", TestCards.StandardAxDeck(), format: "frontier", isPublic: true)));
        Assert.True(created["legal"]!.GetValue<bool>());
        var season = Assert.Single(created["validSeasons"]!.AsArray());
        Assert.Equal(("frontier", "S4"), (season!["format"]!.GetValue<string>(), season["season"]!.GetValue<string>()));

        // Data recovery: past seasons are checked too, from the deck's creation date on.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeckDbContext>();
            var stored = await db.Decks.SingleAsync(d => d.Id == Guid.Parse(created["id"]!.GetValue<string>()));
            stored.CreatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero); // during S2
            await db.SaveChangesAsync();
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/decks/revalidate?format=frontier&seasons=all");
        request.Headers.Add("X-Api-Key", DeckApiFactory.MaintenanceKey);
        var report = await Json(await factory.CreateClient().SendAsync(request));
        Assert.True(report["seasonsAdded"]!.GetValue<int>() >= 2);

        var deck = await Json(await client.GetAsync($"/api/decks/{created["id"]}"));
        Assert.Equal(["S4", "S3", "S2"], deck["validSeasons"]!.AsArray().Select(s => s!["season"]!.GetValue<string>()));

        // Searchable by season.
        var anonymous = factory.CreateClient();
        var page = await Json(await anonymous.GetAsync("/api/decks/public?season=S3&format=frontier&itemsPerPage=1000"));
        Assert.Contains(page["member"]!.AsArray(), m => m!["id"]!.GetValue<string>() == created["id"]!.GetValue<string>());
        page = await Json(await anonymous.GetAsync("/api/decks/public?season=frontier:S2&itemsPerPage=1000"));
        Assert.Contains(page["member"]!.AsArray(), m => m!["id"]!.GetValue<string>() == created["id"]!.GetValue<string>());
        page = await Json(await anonymous.GetAsync("/api/decks/public?season=S9&itemsPerPage=1000"));
        Assert.DoesNotContain(page["member"]!.AsArray(), m => m!["id"]!.GetValue<string>() == created["id"]!.GetValue<string>());

        var formats = await JsonArr(await anonymous.GetAsync("/api/formats"));
        var frontier = formats.Single(f => f!["code"]!.GetValue<string>() == "frontier")!;
        Assert.Equal("S4", frontier["season"]!.GetValue<string>());
        Assert.Equal(["S1", "S2", "S3", "S4"], frontier["seasons"]!.AsArray().Select(s => s!["code"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Deck_permissions_come_from_token_claims()
    {
        var owner = factory.ClientFor("user-claims");
        var created = await Json(await owner.PostAsJsonAsync("/api/decks", DeckBody("Private", TestCards.StandardAxDeck())));

        // Same user, token without the deck permissions.
        var noClaims = factory.ClientFor("user-claims", scope: "openid");
        Assert.Equal(HttpStatusCode.Forbidden, (await noClaims.GetAsync("/api/decks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noClaims.GetAsync($"/api/decks/{created["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noClaims.PostAsJsonAsync("/api/decks", DeckBody("x", TestCards.StandardAxDeck()))).StatusCode);

        // Read-only token: can read, cannot write.
        var readOnly = factory.ClientFor("user-claims", scope: "read-decks");
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"/api/decks/{created["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"/api/decks/{created["id"]}")).StatusCode);
    }

    [Fact]
    public async Task Decks_are_stored_canonical_and_old_clients_read_back_their_prints()
    {
        var client = factory.ClientFor("user-canonical");
        var lines = TestCards.StandardAxDeck();
        lines[0] = new DeckLine("ALT_CORE_P_AX_01_C", 1);                  // promo hero
        lines[1] = new DeckLine("ALT_CORE_A_AX_04_C", 2);                  // alt art...
        lines.Insert(2, new DeckLine("ALT_CORE_B_AX_04_C", 1));            // ...+ 1 base copy
        lines[3] = new DeckLine("ALT_ALIZE_B_AX_05_C", 3);                 // reprint

        var created = await Json(await client.PostAsJsonAsync("/api/decks", DeckBody("Prints", lines, isPublic: true)));
        Assert.True(created["legal"]!.GetValue<bool>());
        Assert.Equal("ALT_CORE_P_AX_01_C", created["stats"]!["hero"]!["reference"]!.GetValue<string>());
        Assert.Equal(2, created["altArtOverrides"]!["cards"]!["ALT_CORE_B_AX_04_C"]![0]!["quantity"]!.GetValue<int>());

        var detail = await Json(await client.GetAsync($"/api/decks/{created["id"]}"));
        // Old clients: the prints they saved.
        Assert.Equal(lines.Select(l => $"{l.Reference}:{l.Quantity}").Order(),
            detail["cards"]!.AsArray().Select(c => $"{c!["cardReference"]}:{c["quantity"]}").Order());
        // New clients: one canonical line per gameplay card.
        Assert.Contains(detail["canonicalCards"]!.AsArray(), c => c!["cardReference"]!.GetValue<string>() == "ALT_CORE_B_AX_04_C" && c["quantity"]!.GetValue<int>() == 3);
        Assert.DoesNotContain(detail["canonicalCards"]!.AsArray(), c => c!["cardReference"]!.GetValue<string>().Contains("_A_"));

        // Any print of a card finds the deck.
        foreach (var search in new[] { "ALT_CORE_B_AX_05_C", "ALT_ALIZE_B_AX_05_C", "ALT_FUGUE_B_AX_05_C" })
        {
            var page = await Json(await factory.CreateClient().GetAsync($"/api/decks/public?cardReference={search}"));
            Assert.Contains(page["member"]!.AsArray(), m => m!["id"]!.GetValue<string>() == created["id"]!.GetValue<string>());
        }

        // The old API received the concrete prints.
        var legacy = Assert.Single(factory.Legacy.Decks.Values, d => d.Owner == DeckApiFactory.BearerOf(client));
        Assert.Contains(legacy.Body["deckCards"]!.AsArray(), c => c!["cardReference"]!.GetValue<string>() == "ALT_CORE_A_AX_04_C");
    }

    [Fact]
    public async Task Formats_expose_sets_and_ban_lists()
    {
        var formats = await JsonArr(await factory.CreateClient().GetAsync("/api/formats"));
        Assert.Equal(["sandbox", "nuc", "standard", "frontier", "singleton", "singleton_nuc", "sealed"], formats.Select(f => f!["code"]!.GetValue<string>()));
        var standard = formats.Single(f => f!["code"]!.GetValue<string>() == "standard")!;
        Assert.Contains(standard["suspendedCards"]!.AsArray(), s => s!.GetValue<string>() == "BR_06_C");
        Assert.Contains(standard["sets"]!["forbidden"]!.AsArray(), s => s!.GetValue<string>() == "FUGUE");
        Assert.Equal(3, standard["limits"]!["maxCopiesPerName"]!.GetValue<int>());
    }
}
