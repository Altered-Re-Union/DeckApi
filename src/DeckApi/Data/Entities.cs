using DeckApi.Validation;

namespace DeckApi.Data;

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Keycloak <c>sub</c> (realm <c>players</c>).</summary>
    public required string KeycloakId { get; set; }
    public string? Email { get; set; }

    /// <summary>Public pseudo: Keycloak <c>pseudo</c> claim only (no fallback, <c>preferred_username</c> can be an email).</summary>
    public string? Username { get; set; }
    public string? Locale { get; set; }
    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class Deck
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Id of the same deck in the old PHP decks-api (null until synced). Equal to <see cref="Id"/> for imported decks.</summary>
    public Guid? LegacyId { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Canonical format code (lowercase), null = none.</summary>
    public string? Format { get; set; }
    public bool IsPublic { get; set; }

    /// <summary>
    /// "Archived": hidden in BGA only. Never affects validation, the owner's listing nor public listings (an
    /// archived deck stays shared). Also set by the legacy sync when the deck disappears from the old API.
    /// </summary>
    public bool IsDraft { get; set; }
    public string? AlteredId { get; set; }

    /// <summary>
    /// <see cref="DeckSource.Tournament"/>: read-only copy of a list played in BGA tournaments, imported from
    /// GameApi and owned by the player's ReUnion account. Independent of the decks the player saved: never in
    /// their own listing, never synchronised with the old API, results in <see cref="TournamentResults"/>.
    /// </summary>
    public DeckSource Source { get; set; }
    public List<DeckTournamentResult> TournamentResults { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // ---- Content ---------------------------------------------------------------------------------

    /// <summary>
    /// The whole deck list (hero included) encoded with the game API's Deckfmt codec (base64url).
    /// Source of truth for the deck content: no join needed to read a deck.
    /// </summary>
    public required string Compressed { get; set; }

    /// <summary>
    /// Lines Deckfmt cannot encode (set unknown to the codec config, number too large for the set's bit
    /// width...). Normally null; keeps the API lossless whatever the old API accepted.
    /// </summary>
    public List<DeckLine>? ExtraCards { get; set; }

    /// <summary>Distinct references and families of the deck, GIN-indexed for "decks containing card X" filters.</summary>
    public string[] CardReferences { get; set; } = [];
    public string[] CardFamilies { get; set; } = [];

    /// <summary>Non-hero card count (old API <c>stats.totalCards</c>).</summary>
    public int CardCount { get; set; }
    public string? HeroReference { get; set; }

    /// <summary>Faction code of the hero (4th segment of its reference), used by the faction filters.</summary>
    public string? HeroFaction { get; set; }

    /// <summary><c>{C,R,U,E}</c> non-hero counts.</summary>
    public RarityCounts ByRarity { get; set; } = new();

    /// <summary>Optional cosmetic overrides (alt arts, tokens). Never affects validation.</summary>
    public AltArtOverrides? AltArtOverrides { get; set; }

    // ---- Validation ------------------------------------------------------------------------------

    public bool Legal { get; set; }

    /// <summary>Untranslated errors (code + reference + parameters); translated at read time.</summary>
    public List<ValidationError>? ValidationErrors { get; set; }

    /// <summary>Ordered rule → ok list (old API <c>legalityDetail</c>, rendered as an object).</summary>
    public List<LegalityRule>? LegalityDetail { get; set; }
    public DateTimeOffset? ValidatedAt { get; set; }

    /// <summary>
    /// Format seasons the deck has been legal in (<c>frontier:S4</c>), GIN-indexed. Only ever grows: a deck
    /// legal in S4 keeps S4 when S5 makes it illegal or when its format changes.
    /// </summary>
    public string[] ValidSeasons { get; set; } = [];

    // ---- Counters --------------------------------------------------------------------------------

    public int ViewCount { get; set; }
    public int UpvoteCount { get; set; }

    // ---- Legacy synchronisation ------------------------------------------------------------------

    /// <summary>Old API's <c>updatedAt ?? createdAt</c> as last seen or written by us (watermark).</summary>
    public DateTimeOffset? LegacyUpdatedAt { get; set; }

    /// <summary>Local changes not pushed to the old API yet (old API down/timeout at write time).</summary>
    public bool LegacySyncPending { get; set; }

    /// <summary>
    /// When the deck was found missing from the old API's listing (deleted there, or not returned). The deck
    /// is archived (<see cref="IsDraft"/>) instead of deleted and no longer pushed to the old API. Cleared when
    /// the deck shows up there again (it takes back the old API's <c>isDraft</c>) or when the owner sets
    /// <c>isDraft: false</c> (it is then recreated on the old API).
    /// </summary>
    public DateTimeOffset? LegacyDeletedAt { get; set; }
}

public enum DeckSource
{
    User = 0,
    Tournament = 1,
}

/// <summary>
/// How a tournament deck did in one tournament (one GameApi player row, one distinct list). Replaced as a
/// whole each time GameApi recomputes that player's row.
/// </summary>
public sealed class DeckTournamentResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DeckId { get; set; }
    public long TournamentParentId { get; set; }
    public string? TournamentName { get; set; }

    /// <summary>BGA format of the tournament, as GameApi reports it (e.g. <c>FRONTIER</c>).</summary>
    public string? Mode { get; set; }

    /// <summary>GameApi's player key. Never rendered: tournament decks are only shown with the ReUnion pseudo.</summary>
    public required string BgaUserId { get; set; }
    public int Games { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }
    public DateTimeOffset ComputedAt { get; set; }
}

/// <summary>Last position read in an external feed (e.g. GameApi's tournament decks).</summary>
public sealed class SyncCursor
{
    public required string Name { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DeckUpvote
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DeckId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A deck deleted locally while the old API was unreachable: deleted there on the next sync.</summary>
public sealed class LegacyPendingDelete
{
    public Guid LegacyId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Last good copy of the non-unique card catalog, so a restart never depends on uniques-search-api.</summary>
public sealed class CardCatalogSnapshot
{
    public int Id { get; set; } = 1;
    public required string Payload { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

public sealed record DeckLine(string Reference, int Quantity);

public sealed class RarityCounts
{
    public int C { get; set; }
    public int R { get; set; }
    public int U { get; set; }
    public int E { get; set; }
}

public sealed record LegalityRule(string Rule, bool Ok);

/// <summary>
/// Cosmetic replacements chosen by the player, same shape as AlteredOwnership's
/// <c>/api/alt-arts/apply-to-deck</c> response so altered-bga-api can apply it as-is.
/// </summary>
public sealed class AltArtOverrides
{
    /// <summary>Base deck reference → the prints to show instead (quantities sum to at most the deck quantity).</summary>
    public Dictionary<string, List<DeckLine>> Cards { get; set; } = new();

    /// <summary>Token prints to use (e.g. an alt-art token).</summary>
    public List<DeckLine> Tokens { get; set; } = [];
}
