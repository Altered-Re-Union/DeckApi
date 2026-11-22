# DeckApi

C# (.NET 10) replacement for the PHP `altered-core-decks-api`. Drop-in compatible for existing clients:
same routes, status codes and JSON shapes. Everything new is additive (extra fields, extra query parameters).

## What changes compared to the PHP API

| Topic | PHP decks-api | DeckApi |
|---|---|---|
| "Same card" for copy limits | localized card **name** | card **family** `{FACTION}_{NUM}` (e.g. `AX_04`), across rarities, sets, alt arts and promos |
| Card reference | `altered-core-cards-api`, one batch call per save | **uniques-search-api**: the non-unique catalog (~3.5k prints) is held in memory and snapshotted in the DB; Uniques are resolved **locally** with altered-draft's serial→faction range tables (`Cards/FactionRanges/*.json`) |
| Format rules | hard-coded per validator class | `Formats/Config/formats.json`: sets, ban/suspension lists, limits, Frontier allowlist, **seasons**. Only sealed pool membership is code |
| Validity errors | English strings in `formatErrors` | `errors: [{code, reference, message}]`, translated with `?locale=` (en default; fr, de, es, it) |
| Deck storage | `deck_card` rows, concrete prints | **canonical** gameplay cards: one reference per family + rarity (booster print of the first set in `CORE, COREKS, ALIZE, BISE, CYCLONE, DUSTER, EOLE, FUGUE, others`; Uniques keep their set + serial), print choices in `altArtOverrides`. Stored as `compressed` (Deckfmt) + GIN-indexed `card_references` / `card_families` |
| Set legality | the deck's print set | the card's family + rarity is printed in **at least one** allowed set (reprints / alt arts / promos of a legal card are legal) |
| Public decks | owner hidden | `owner.pseudo` on public listings and public deck pages: the Keycloak `pseudo` claim only, never `preferred_username`/`name` (can be an email) |
| Drafts (`isDraft`) | not validated (`legal=false`), hidden from public listings | means **hidden in BGA** only: always validated, listed publicly like any other deck. Public listings exclude **illegal** decks instead |
| Cosmetics | — | optional `altArtOverrides` (alt arts and tokens) |
| Revalidation | CLI command | `POST /api/admin/decks/revalidate?format=…` with `X-Api-Key` |
| BGA endpoints | in decks-api | in `altered-bga-api` (branch `feat/deck-api-bridge`), reading DeckApi + uniques-search-api |

## Endpoints

These behave like the old API:
- `GET /api/decks` returns the caller's decks as a plain array. Added filters: `format` (repeatable or comma-separated, aliases accepted), `legal`, `isDraft`, `cardReference`, `family`.
- `POST /api/decks`, `PATCH|PUT|DELETE /api/decks/{id}`. `{id}` also accepts an old-API id.
- `GET /api/decks/{id}` returns `cards` (card detail) or `deckCards: []` when the deck is empty.
- `GET /api/decks/public` has the old pagination body and the old filters, plus `family`. `name` is now case-insensitive. It lists public **legal** decks, drafts included; `legal` is still accepted but illegal decks are never listed. Added: `source=user|tournament|all` (default `user`, the old behaviour) and `season`.
- `GET /api/decks/public/heroes`, `POST /api/decks/{id}/upvote`, `GET /api/me`.
- `GET /api/admin/stats` and `GET /api/admin/decks` require `users.is_admin`.
- `GET /api/formats` returns the old keys plus `maxFactions`, `sets.allowed` / `sets.forbidden`, `bannedCards`, `suspendedCards`, `uniqueAllowlist`, `season` (current season code) and `seasons` (`code`, `label`, `start`, `end`). A front can display or pre-check the rules from it instead of re-implementing them.

Deck permissions are Keycloak client scopes. They are read from the access token's `scope` claim, the same convention as AlteredOwnership's `read-collection` / `write-collection`.
- `read-decks` is required to list your own decks and to read a private deck.
- `write-deck` is required to create, update or delete a deck.
- Public reads (public decks, public listing, heroes, formats) stay anonymous.
- The names are configurable with `Auth:ReadDecksScope` and `Auth:WriteDecksScope`.
- `Auth:RequireDeckScopes=false` disables the check, for example while clients are being given the scopes.

altered-bga-api can read any deck by id with `X-Api-Key: <ApiKeys:Bga>`, like the old public `GET /api/bga/decks/{id}`.

These are new:
- `POST /api/admin/decks/revalidate?format=standard[&seasons=all]` with header `X-Api-Key: <ApiKeys:Maintenance>`. It recomputes the legality of every deck of that format (drafts included) and returns counters. Sealed decks are skipped, because their pool can only be read with the owner's token.
- `GET /healthz`.

### Archived decks

Archiving a deck is `isDraft: true`. It hides the deck in BGA only: it stays in `GET /api/decks` (`isDraft=true` lists the archive), in the public listings and readable by anyone when public. A deck under construction is not a draft, it is simply illegal.

When the legacy sync archives a deck because it disappeared from the old API, it also sets `legacyDeletedAt`. That deck is no longer pushed to the old API, even when edited. `isDraft: false` clears `legacyDeletedAt` and recreates the deck there, with a new old-API id.

### Tournament decks

DeckApi imports the decks played in BGA tournaments from GameApi (`GET {GameApi:BaseUrl}/api/tournament-decks`,
`Authorization: Bearer <GameApi:ApiKey>`), every `GameApi:SyncMinutes` and on
`POST /api/admin/tournament-decks/sync` (`X-Api-Key: <ApiKeys:Maintenance>`).

- They are ordinary deck rows with `source: "tournament"`: stored canonical, public, validated, so every public
  filter applies. `GET /api/decks/public?source=all` is the unified search; the default stays `source=user`.
- Each one is a read-only copy owned by the player's **ReUnion** account (created with GameApi's ReUnion pseudo
  when the player never used DeckApi). It is independent of the decks the player saved: it is not in
  `GET /api/decks` (so not in BGA either), not synchronised with the old API, and cannot be edited or deleted.
  The link is the ReUnion account, never a deck id nor a list comparison with the player's own decks.
- One deck per owner + format + canonical list. `tournaments: [{ tournamentParentId, name, mode, games, wins,
  losses, lastPlayedAt }]` holds its record in each tournament (per list, without admin adjustments).
- The season a result was played in is added to `validSeasons` when the list was legal under that season's rules.
- Tournament decks stay listed when today's rules make them illegal: they are records of what was played.
- Players GameApi could not link to a ReUnion account are skipped. The BGA name is never stored nor shown.
- A GameApi row replaces all the results it produced before; decks left without any result are deleted.

### Seasons

A format can declare `seasons` in `formats.json`, oldest first (Frontier: S2, S3, S4). A season replaces the
unique allowlist and can add bans and suspensions on top of the format's lists, so a ban can be shipped as a
season rotation for any format. The current season is the last one whose `start` is past (no `start` counts as
started); a deck is validated with the current season's rules.

- A deck legal in the current season gets it in `validSeasons` (`[{ "format": "frontier", "season": "S4" }]`).
  The list only grows: a deck legal in S4 keeps S4 when S5 makes it illegal.
- `revalidate?format=frontier&seasons=all` also checks every started past season, newest first, skipping the
  seasons that ended before the deck was created. This is the data recovery for existing decks.
- `GET /api/decks/public?season=frontier:S4` (or `season=S4&format=frontier`) lists the decks legal in a season.
- The allowlists are copies of the prod formats bucket that uniques-search-api reads
  (`https://altered-reunion-formats-prod.s3.fr-par.scw.cloud/frontier.sNN.json`, S1 to S4). The bucket's `frontier.json`
  is not used: the season files are explicit.

### Additive fields on a deck

```jsonc
{
  // ...all the old deck:read fields, same order...
  "errors": [{ "code": "card.family.copies", "reference": "ALT_CORE_B_AX_04_C", "message": "Card \"…\" exceeds 3 copies (got 4 across all rarities and prints)." }],
  "compressed": "IBAk3gx0hL…",            // Deckfmt (hero included)
  "altArtOverrides": {                    // optional, null by default
    "cards": { "ALT_CORE_B_AX_04_C": [{ "reference": "ALT_CORE_A_AX_04_C", "quantity": 2 }] },
    "tokens": [{ "reference": "ALT_CORE_A_BR_31_C", "quantity": 1 }]
  },
  "owner": { "pseudo": "…" }              // public contexts only
}
```

- `canonicalCards` (item GET): the stored list, one canonical reference per gameplay card. The old `cards` / `deckCards` fields keep returning the concrete prints (overrides applied), so old clients read back what they saved. Searching decks by `cardReference` accepts any print of the card.
- On write, any print in `deckCards` (alt art, promo, reprint) is stored as its canonical card plus an override. A sent `deckCards` is the whole truth for cards and prints; `altArtOverrides` sent alongside apply on top. New clients send canonical references + `altArtOverrides`.
- `formatErrors` now holds the translated messages of `errors`.
- `legalityDetail` gains a `cards` key. It is false when a reference does not exist, for example a Unique serial outside its range.
- `altArtOverrides` are the deck's own art choices, with the highest priority. altered-bga-api sends them to AlteredOwnership's `apply-to-deck` as **pinned** lines, together with any alt-art/promo print stored directly in the deck list (product other than `B`).
  - The player's global preferences only apply to the other lines. There is no Global/PerDeck mode anymore.
  - Ownership is always checked: copies the player doesn't own fall back to the default art.
- Writes accept `compressed` instead of `deckCards`. They also accept `altArtOverrides`:
  - Each replacement must be a print of the same family and rarity.
  - Replacements cannot cover more copies than the deck holds.
  - Old clients that never send this field keep working. When they change the list, overrides for cards removed from the deck are dropped.

Error codes are listed in `Validation/ValidationError.cs`. Message templates are in `Validation/Messages/{lang}.json`; adding a language means adding one file.

## Synchronisation with the old API

The old API is never a hard dependency. Every call to it has a `Legacy:TimeoutMs` timeout (default 2 s) and forwards the caller's own Keycloak bearer.

- **Writes** (create, update, delete) are replayed on the old API.
  - If the old API fails, the deck is saved locally and flagged `legacy_sync_pending`. A failed delete is recorded in `legacy_pending_deletes`.
  - Both are retried on the user's next listing.
  - A deck created while the old API is down gets a local id that differs from its eventual old-API id.
- **Reads**: `GET /api/decks` first lists the user's decks on the old API.
  - Decks created or modified there since the last watermark (`legacy_updated_at`) are pulled in.
  - Decks missing there are **archived** here (`isDraft: true`, `legacyDeletedAt` set), never deleted. A listing that is partial one day (pagination, bug) cannot destroy decks: a deck that shows up again is restored with the old API's `isDraft`.
  - When both sides changed, the last writer wins.
  - At most `Legacy:MaxPullsPerSync` deck details are fetched per call.
- A deck first seen on the old API keeps its id here (`id = legacy_id`). So do imported decks.
- Not synchronised: upvotes, view counts and alt-art overrides (the old API has no field for them).

## Importing the old database

```powershell
./scripts/import-legacy.ps1 -Backup ./decks.dump -Target "Host=…;Database=deckapi;…" -UniquesSearchUrl https://…
```
```bash
UniquesSearch__BaseUrl=https://… scripts/import-legacy.sh ./decks.dump "Host=…;Database=deckapi;…"
```

The script runs these steps:
1. Restores the backup (`pg_dump` custom/tar format, `.sql` or `.sql.gz`) into a throwaway `postgres:17` container.
2. Migrates the target database.
3. Runs `DeckApi import-legacy`.

What the import does:
- Users are matched by Keycloak id. Decks keep their id.
- Content is re-encoded and legality is recomputed with the new rules.
- Sealed decks keep the backup's `legal` flag.
- Upvotes are copied.
- It is re-runnable: a deck is only updated when the backup is newer than what was last synced. `--dry-run` writes nothing.

Lines that Deckfmt cannot encode are kept uncompressed in `extra_cards` and logged. Examples: a set unknown to the latest `Deckfmt/Config/vN.json`, or a number too large for the set's bit width.

Deckfmt codec versions (shared with altered-bga-api and alteredcore-website's tournament-reports decoder; set ids MUST stay identical in all three):

- **v1**: initial layout. Frozen, so already-stored blobs keep decoding.
- **v2** (current): MUSUBI goes from 5 to 7 number bits, because its numbers go up to 74. Also includes `EOLETOP` (20) and `EOLEOP` (21). Decks saved under v1 with MUSUBI cards in `extra_cards` still read correctly, and those cards move into `compressed` the next time the deck is saved.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:decks` | — | required |
| `Database:MigrateOnStartup` | false | otherwise use the `migrations` image |
| `Keycloak:BaseUrl` / `Keycloak:Realm` | `https://auth.altered.re` / `players` | JWKS + issuer check, no audience check (same as the old API) |
| `UniquesSearch:BaseUrl` | — | required |
| `Cards:RefreshMinutes` | 60 | catalog refresh; the last good copy is kept in `card_catalog_snapshots` |
| `Cards:ImageUrlTemplate` | empty | `{reference}` and `{locale}` placeholders for `imagePath`; null when empty |
| `Formats:ConfigPath` | empty | directory with a `formats.json` (and allowlists) overriding the embedded one; run the revalidate endpoint afterwards |
| `AlteredDraft:BaseUrl` | `https://altered-draft.altered.re` | sealed pools; unavailable means sealed decks are invalid (fail closed) |
| `Legacy:BaseUrl` | `https://decks.alteredcore.org` | empty disables the synchronisation |
| `Legacy:TimeoutMs` | 2000 | |
| `ApiKeys:Maintenance` | empty | key for the revalidate endpoint; empty means the endpoint is closed |
| `ApiKeys:Bga` | empty | altered-bga-api's key to read any deck by id |
| `GameApi:BaseUrl` | empty | GameApi; empty disables the tournament deck import |
| `GameApi:ApiKey` | empty | GameApi's `ApiKeys:DeckApi` |
| `GameApi:SyncMinutes` | 10 | import interval; 0 = only through the admin endpoint |
| `GameApi:PageSize` | 200 | |
| `Auth:RequireDeckScopes` | true | require the `read-decks` / `write-deck` scopes |
| `Auth:ReadDecksScope` / `Auth:WriteDecksScope` | `read-decks` / `write-deck` | scope names |
| `Cors:AllowOriginRegex` | empty (any) | same semantics as `CORS_ALLOW_ORIGIN` |
| `Auth:DevSigningKey` | — | non-production only: HS256 tokens with `iss: dev` |

## Run and test

```bash
docker run -d --name deckapi-db -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=deckapi -p 5432:5432 postgres:17-alpine
dotnet run --project src/DeckApi          # Development: migrates on startup, uniques-search-api on :8234
dotnet test                               # needs Docker (Testcontainers)
```

Images: `build/Dockerfile` builds two targets, `app` and `migrations` (an EF bundle), the same layout as altered-bga-api.

## Known gaps / to decide

- **Card detail** (`cards[]` on item GET): uniques-search-api has no image path and no trigger/condition/effect split.
  - `imagePath` comes from `Cards:ImageUrlTemplate`.
  - `effects[]` holds the full main and echo texts, with the ability parts set to null.
- **Unique ban list**: only the 20 suspended non-unique prints of the current catalog are seeded, as family+rarity entries. Unique-specific bans must be added to `formats.json`.
- **Sealed**: altered-draft creates sealed decks on this API and links each pool to the deck's `id`. The pool lookup sends `id`, then `legacy_id` as a fallback. Imported decks kept their old-API id, so the pools linked before the switch still match. A deck recreated on the old API (new old-API id) keeps its pool. A 404 from altered-draft (deck not linked yet) is not cached: altered-draft links a deck right after creating it.
