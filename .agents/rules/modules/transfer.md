# Module: Transfer

Import/export of tenant data as `fsh.transfer/1` zip archives; other modules plug in a source and/or sink per entity type. Module `Order = 920`. **Off by default** (`Modules:Transfer:Enabled`).

**Entity / DbContext:** `TransferJob` in `transfer.transfer_jobs` (`TransferDbContext`): `Kind` (Export/Import), `Status` (Pending/Running/Succeeded/Failed), `EntityTypes` (jsonb), `Mode?`, `StoragePath`, `FileName`, `SizeBytes`, `Sha256` (of the whole zip), `Summary` (jsonb: rows per type, created/updated/skipped/failed, first 100 row errors), `Error?`, `CreatedBy`, `CreatedAtUtc`, `CompletedAtUtc?`. **No job or archive is ever deleted automatically**, and there is no mirror/delete import mode.
**Endpoints** (`/api/v1/transfer`):

| Route | Permission | |
|---|---|---|
| `GET /entity-types` | signed in | registered types + `canExport`/`canImport` for the caller |
| `POST /exports {entityTypes[]}` | `Transfer.Export` + each source's | 202, job runs on Hangfire |
| `GET /jobs/{id}` | `Transfer.Export` or `.Import` (by kind) | |
| `GET /exports/{id}/download` | `Transfer.Export` + each source's | streams the zip; 409 until Succeeded |
| `POST /imports` (multipart field `file`) | `Transfer.Import` + each sink's | validates the whole archive, stores it, job Pending; 400 on any archive problem |
| `POST /imports/{id}/preview {mode}` | same | synchronous, writes nothing |
| `POST /imports/{id}/apply {mode}` | same | 202; only Pending or Failed imports (atomic claim, else 409); Hangfire |

`mode` is `CreateOnly` or `Upsert` and is required (0 is undefined on purpose).

## Archive format and limits

`manifest.json` `{format: "fsh.transfer/1", createdAtUtc, files: [{path, entityType, rows, sha256}]}` + `data/{entityType}.ndjson` (one JSON object per line). Entity types: lowercase `[a-z0-9]` words joined by `.`, `-`, `_`, ≤ 64 chars (e.g. `catalog.brands`). `TransferArchive.Open` rejects the **whole** archive (400) on: more than `MaxEntries` (1000, read from the end record before the directory), unsafe entry names (absolute, `..`, `.`, empty segment, `\`, `:`, control chars), duplicate or unlisted entries (directory entries included), total inflated bytes over `MaxUncompressedBytes` (1 GiB, counted from the bytes actually inflated, not the headers), ratio > `MaxCompressionRatio` (100) for entries over 1 MiB, wrong format, SHA-256 or row-count mismatch, a line over `MaxLineBytes` (1 MiB) or not a JSON object. Uploads over `MaxUploadBytes` (200 MiB) are refused by the endpoint (request size + form limit) and by the handler, which counts the bytes it actually received. Section `TransferOptions`, validated on start. Nothing is extracted to disk; archives sit in seekable temp files deleted on close.

## Adding a source/sink (another module)

Reference `Modules.Transfer.Contracts` only, implement `ITransferSource` / `ITransferSink`, and register them **scoped** in the module's `ConfigureServices` (`AddScoped<ITransferSource, X>()`). Last registration per entity type wins. The sink gets rows already validated as JSON objects; it must check every field itself, count bad rows with `TransferTally.Fail` (never throw for a row), match without deleting, and save its own changes. Reference implementation: Catalog `Transfer/BrandTransfer.cs`.

Registering costs nothing when Transfer is off (only Transfer resolves these), and when the owning module is off its types just disappear: exports of them fail with 400 and archives containing them are refused at upload.

## Gotchas

- **Private storage without `IStorageService.Upload`.** `IStorageService` cannot write a server-side stream to a private key (`UploadAsync<T>` goes to `public/`, in memory, images/docs only), so `ArchiveStore` writes through the provider underneath (`IAmazonS3` + `Storage:S3` bucket/prefix, or the local web root) at `private/tenants/{tenant}/transfer/{jobId}.zip`, and reads back through `IStorageService.DownloadAsync`. Consequences: archives are **not charged to the storage quota**, and with the **local provider they are reachable under `/private/...`** of the web root (dev only, like the rest of local storage; the key holds an unguessable job id). Fixing it properly means a private `PutAsync` on `IStorageService` (BuildingBlocks, needs approval).
- **Jobs run without HTTP context.** `TransferJobRunner` takes the tenant id as an argument, opens its own scope, sets the Finbuckle context and the creator as current user, then resolves anything tenant-filtered. No automatic retries (`AutomaticRetry(Attempts = 0)`): a failure is stored on the job, and only `CustomException` messages reach the user (others: "see the server logs for job …").
- **A job can stay `Running`** if the process dies mid-run and Hangfire drops the job; re-running is safe (create/upsert only) but has to be done by resetting `Status` to `Failed` by hand.
- Permissions are checked per request (upload, preview, apply, download), not again inside the job.
- The client's upload file name is display-only: last path segment, `[A-Za-z0-9._-]`, ≤ 100 chars. Download names are generated (`transfer-export-{yyyyMMdd-HHmmss}.zip`).
- `GET /exports/{id}/download` re-checks each source permission, because the archive holds rows of every exported type.

## Catalog brands (`catalog.brands`)

Rows `{"name", "description"}` (logo URLs are not exported). Source needs `Catalog.Brands.View`, sink `Catalog.Brands.Create`. Matching is by **slug**, i.e. by name the way Catalog keeps names unique (`ACME` matches `Acme`); the existing name is kept. `CreateOnly` skips matches; `Upsert` sets the description of a match when the row has a `description` key (null clears it), else skips it. Note that Upsert lets `Brands.Create` holders edit descriptions. The sink loads the tenant's live brands into memory (fine for thousands, see the `ponytail:` note).
