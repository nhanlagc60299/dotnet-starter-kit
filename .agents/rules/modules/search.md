# Module: Search

Vietnamese full-text search over an index other modules feed, plus slug/normalisation helpers. Module `Order = 900`. **Off by default** (`Modules:Search:Enabled`).

**Entity / DbContext:** `SearchDocument` in `search.search_documents` (`SearchDbContext`), unique per (tenant, `EntityType`, `EntityId`). Derived data: owners re-index or remove entries, so removal is a hard delete.
**Endpoints** (`/api/v1/search`): `GET /?q=&entityType=&pageNumber=&pageSize=` (`Search.View`), `PUT /documents` upsert (`Search.Manage`), `DELETE /documents/{entityType}/{entityId}` idempotent (`Search.Manage`). Other modules send `IndexSearchDocumentCommand` / `RemoveSearchDocumentCommand` through Mediator instead.
**Contracts helpers:** `SlugHelper.Generate`, `TextNormalizer.Normalize` (lowercase, unaccented, đ→d), `SafeNormalization.SafeNormalize` in `Contracts/Text` — use these instead of writing new ones.

## Gotchas

- **The initial migration has hand-written SQL before `CreateTable`:** `CREATE EXTENSION unaccent` and the `search.vi` text search configuration (`simple` with `unaccent` in front). The STORED generated `Tsv` column (title A, summary B, body C) references `search.vi`, so the config must exist first. `unaccent` is a trusted extension (database owner is enough, no superuser).
- **Reads stay in LINQ** (`EF.Functions.ToTsQuery("search.vi", …)`, `Matches`, `RankCoverDensity`, `GetResultHeadline`) so the tenant query filter applies. Never switch the read path to raw SQL.
- Query text is parsed by `SearchText` (slice `SearchDocuments`): `"phrase"`, `-exclude`, `or`, last word is a prefix; every token is reduced to `[a-z0-9]`, so no tsquery syntax gets through. Rank = `ts_rank_cd` + 2 × the typed-phrase rank ("học sinh" ranks above "sinh học").
- `Snippet` wraps matches in `<mark>`…`</mark>` but the rest is **not HTML-encoded**: render by splitting on the markers, never as innerHTML.
- `Search.View` is not a basic permission: a hit shows the title of whatever was indexed, whatever the caller may see in the owning module.
- Upsert is read-then-write; two concurrent *first* indexes of the same entity can hit the unique index (one 500, retry is safe).
