using System.Text;
using System.Text.Json;
using DeckApi.Cards;
using DeckApi.Data;
using DeckApi.Formats;
using DeckApi.Validation;

namespace DeckApi.Tests.Formats;

public class FormatSeasonsTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private const string S1Unique = "ALT_CORE_B_AX_04_U_1";
    private const string S2Unique = "ALT_CORE_B_AX_04_U_2";

    private static readonly CardCatalog Catalog = TestCards.LoadedCatalog();

    private static FormatCatalog Seasonal(DateTimeOffset now)
    {
        const string config = """
        {
          "shared": { "suspended": ["BR_06"] },
          "formats": [{
            "code": "frontier", "label": "Frontier", "minCards": 39, "maxCards": 59,
            "maxCopiesPerFamily": 3, "maxUniques": 3, "uniqueSingleCopy": true,
            "seasons": [
              { "code": "S1", "label": "Season 1", "start": "2026-01-01T00:00:00Z", "end": "2026-06-01T00:00:00Z", "uniqueAllowlist": "s1.json" },
              { "code": "S2", "label": "Season 2", "start": "2026-06-01T00:00:00Z", "uniqueAllowlist": "s2.json", "banned": ["AX_30"] },
              { "code": "S3", "label": "Season 3", "start": "2027-01-01T00:00:00Z", "uniqueAllowlist": "s2.json" }
            ]
          }]
        }
        """;
        var files = new Dictionary<string, string>
        {
            ["s1.json"] = $$"""{ "included_refs": ["{{S1Unique}}"] }""",
            ["s2.json"] = $$"""["{{S2Unique}}"]""",
        };
        return new FormatCatalog(
            JsonSerializer.Deserialize<FormatConfigFile>(config, FormatCatalog.JsonOptions)!,
            name => files.TryGetValue(name, out var json) ? new MemoryStream(Encoding.UTF8.GetBytes(json)) : null,
            new FixedClock(now));
    }

    private static List<DeckLine> DeckWith(string unique)
    {
        var deck = TestCards.StandardAxDeck();
        deck[1] = deck[1] with { Quantity = 2 };
        deck.Add(new(unique, 1));
        return deck;
    }

    private static bool Legal(FormatDefinition format, List<DeckLine> lines) =>
        DeckValidator.Validate(format, lines,
            Catalog.ResolveAsync(lines.Select(l => l.Reference), CancellationToken.None).GetAwaiter().GetResult()).Legal;

    [Fact]
    public void Current_season_is_the_last_started_one()
    {
        Assert.Equal("S1", Seasonal(new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)).Find("frontier")!.Season!.Code);
        Assert.Equal("S2", Seasonal(new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero)).Find("frontier")!.Season!.Code);
        // Before any season: the first one.
        Assert.Equal("S1", Seasonal(new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)).Find("FRONTIER")!.Season!.Code);
    }

    [Fact]
    public void Seasons_of_lists_started_seasons_only()
    {
        var formats = Seasonal(new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(["S1", "S2"], formats.SeasonsOf("frontier").Select(f => f.Season!.Code));
        Assert.Empty(formats.SeasonsOf("standard"));
    }

    [Fact]
    public void Each_season_has_its_own_allowlist_and_bans()
    {
        var formats = Seasonal(new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero));
        var (s1, s2) = (formats.SeasonsOf("frontier")[0], formats.SeasonsOf("frontier")[1]);

        Assert.True(Legal(s1, DeckWith(S1Unique)));
        Assert.False(Legal(s2, DeckWith(S1Unique)));
        Assert.True(Legal(s2, DeckWith(S2Unique)));

        // S2 bans AX_30 on top of the shared suspension list.
        Assert.Contains("AX_30", s2.EffectiveBanned);
        Assert.Contains("BR_06", s2.EffectiveSuspended);
        Assert.DoesNotContain("AX_30", s1.EffectiveBanned);
        var withBanned = TestCards.StandardAxDeck();
        withBanned[^1] = new("ALT_CORE_B_AX_30_C", 3);
        Assert.True(Legal(s1, withBanned));
        Assert.False(Legal(s2, withBanned));
    }

    [Fact]
    public void A_deck_is_only_checked_against_seasons_it_could_have_been_played_in()
    {
        var s1 = Seasonal(new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero)).SeasonsOf("frontier")[0].Season!;
        Assert.True(s1.CouldHaveBeenPlayed(new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(s1.CouldHaveBeenPlayed(new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Embedded_frontier_has_the_prod_seasons()
    {
        var formats = FormatCatalog.Load(null);
        Assert.Equal(["S1", "S2", "S3", "S4"], formats.SeasonsOf("frontier").Select(f => f.Season!.Code));
        var current = formats.Find("frontier")!;
        Assert.Equal("S4", current.Season!.Code);
        Assert.Equal(30000, current.UniqueAllowlistRefs!.Count);
        Assert.Null(formats.Find("standard")!.Season);
    }
}
