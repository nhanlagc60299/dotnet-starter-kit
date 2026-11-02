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

`manifest.json` `{format: "fsh.transfer/1", createdAtUtc, files: [{path, entityType, rows, sha256}]}` + `data/{entityType}.ndjson` (one JSON object per line). Entity types: lowercase `[a-z0-9]` words joined by `.`, `-`, `_`, ≤ 64 chars (e.g. `catalog.brands`). `TransferArchive.Open` rejects the **whole** archive (400) on: more than `MaxEntries` (1000, read from the end record before the directory), unsafe entry names (absolute, `..`, `.`, empty segment, `\`, `:`, control chars), duplicate or unlisted entries (directory entries included), total inflated bytes over `MaxUncompressedBytes` (1 GiB, counted from the bytes actually inflated, not the headers), ratio > `MaxCompressionRatio` (100) for entries over 1 MiB, wrong format, SHA-256 or row-count mismatch, a file with more than `MaxRowsPerFile` rows (100 000, checked against the manifest and counted while reading), a line over `MaxLineBytes` (1 MiB) or not a JSON object (duplicate keys included). The export writer refuses the same row/line limits. Uploads over `MaxUploadBytes` (200 MiB) are refused by the endpoint (request size + form limit) and by the handler, which counts the bytes it actually received. Section `TransferOptions`, validated on start. Nothing is extracted to disk; archives sit in seekable temp files deleted on close. Preview runs in the request, bounded by these limits.

## Adding a source/sink (another module)

Reference `Modules.Transfer.Contracts` only, implement `ITransferSource` / `ITransferSink`, and register them **scoped** in the module's `ConfigureServices` (`AddScoped<ITransferSource, X>()`). Last registration per entity type wins. The sink gets rows already validated as JSON objects; it must check every field itself, count bad rows with `TransferTally.Fail` (never throw for a row), match without deleting, and save its own changes. Reference implementation: Catalog `Transfer/BrandTransfer.cs`.

Registering costs nothing when Transfer is off (only Transfer resolves these), and when the owning module is off its types just disappear: exports of them fail with 400 and archives containing them are refused at upload.

## Gotchas

- **Archive storage (`ArchiveStore`).** Key `private/tenants/{tenant}/transfer/{jobId}.zip`. **Production uses S3** (`Storage:Provider=s3`; the deploy compose refuses the local provider): `IStorageService` cannot write a server-side stream to a private key (`UploadAsync<T>` goes to `public/`, in memory, images/docs only), so the archive is written with the registered `IAmazonS3` under `Storage:S3`'s bucket/prefix and read back through `IStorageService.DownloadAsync`. With the **local provider** archives go to `TransferOptions:LocalDirectory` (default `App_Data/transfer` under the content root) and are read from there; **never the web root**, which `UseStaticFiles` serves anonymously before authentication (a directory overlapping it is refused at use). Mount that directory on a volume if you run local storage anywhere persistent. A private `PutAsync` on `IStorageService` (BuildingBlocks, needs approval) would remove the S3 special case.
- **Quota.** Every archive write is charged to the tenant's `StorageBytes` quota (`IQuotaService.CheckAndRecordAsync`, 507 when exceeded, rolled back if the write fails), like `QuotaMeteredStorageService`. Archives are never deleted, so the charge is never refunded.
- **Jobs run without HTTP context.** `TransferJobRunner` takes the tenant id as an argument, opens its own scope, sets the Finbuckle context and the creator as current user, then resolves anything tenant-filtered. No automatic retries (`AutomaticRetry(Attempts = 0)`): a failure is stored on the job, and only `CustomException` messages reach the user (others: "see the server logs for job …").
- **A job can stay `Running`** if the process dies mid-run and Hangfire drops the job; re-running is safe (create/upsert only) but has to be done by resetting `Status` to `Failed` by hand.
- Permissions are checked per request (upload, preview, apply, download) and **again inside the job** as its creator (who may have lost them since); a failed check fails the job.
- **Any `Transfer.Export` holder in a tenant can see and download every export job of that tenant** (plus the source permissions of its types); jobs are not private to their creator.
- The client's upload file name is display-only: last path segment, `[A-Za-z0-9._-]`, ≤ 100 chars. Download names are generated (`transfer-export-{yyyyMMdd-HHmmss}.zip`).
- `GET /exports/{id}/download` re-checks each source permission, because the archive holds rows of every exported type.
- `GET /jobs/{id}` needs only `Transfer.Export` (export jobs) or `Transfer.Import` (import jobs), not each source's/sink's permission: an import job's summary (row counts, first 100 row errors) is visible to any `Transfer.Import` holder who has its id.
- **Finding jobs (runbook).** There is no list endpoint; the dashboard only remembers the last export/import id per browser tab (sessionStorage). An operator finds a tenant's jobs in `transfer.transfer_jobs` (filter on `"TenantId"`, order by `"CreatedAtUtc"`), e.g. to hand a user the id of a finished export instead of letting them export again: exports count against quota and are never refunded.

## Catalog brands (`catalog.brands`)

Rows `{"name", "description"}` (logo URLs are not exported). Source needs `Catalog.Brands.View`, sink `Catalog.Brands.Create`. Matching is by **slug**, i.e. by name the way Catalog keeps names unique (`ACME` matches `Acme`); the existing name is kept. `CreateOnly` skips matches; `Upsert` sets the description of a match when the row has a `description` key (null clears it), else skips it. Note that Upsert lets `Brands.Create` holders edit descriptions. The sink keeps a slug → (id, description) index of the tenant's live brands in memory and, when applying, saves every 1 000 changes and clears the change tracker, so a failure part-way keeps the batches already saved (re-running is safe).
