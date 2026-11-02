using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FSH.Modules.Catalog.Contracts.Authorization;
using FSH.Modules.Catalog.Data;
using FSH.Modules.Catalog.Domain;
using FSH.Modules.Transfer.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Catalog.Transfer;

/// <summary>Brands as Transfer rows: <c>{"name": "...", "description": "..."}</c>. Logo URLs are not exported.</summary>
internal static class BrandTransfer
{
    public const string EntityType = "catalog.brands";
}

internal sealed class BrandTransferSource(CatalogDbContext dbContext) : ITransferSource
{
    public string EntityType => BrandTransfer.EntityType;
    public string Permission => CatalogPermissions.Brands.View;

    public async IAsyncEnumerable<JsonObject> ExportAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var brands = dbContext.Brands.AsNoTracking()
            .OrderBy(b => b.Name)
            .Select(b => new { b.Name, b.Description })
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false);
        await foreach (var brand in brands)
        {
            yield return new JsonObject { ["name"] = brand.Name, ["description"] = brand.Description };
        }
    }
}

/// <summary>
/// Matches rows to live brands by slug, i.e. by name the way Catalog keeps names unique ("ACME" matches "Acme").
/// CreateOnly creates unknown brands and skips known ones; Upsert also sets the description of known ones (when the row
/// has a <c>description</c> key; null clears it). Names of existing brands are never changed and nothing is deleted.
/// Needs <c>Catalog.Brands.Create</c>; note that Upsert therefore lets that permission edit descriptions. Applying saves
/// every 1,000 changes, so a failure part-way keeps the batches already saved (re-running is safe: create/upsert only).
/// </summary>
internal sealed class BrandTransferSink(CatalogDbContext dbContext) : ITransferSink
{
    public string EntityType => BrandTransfer.EntityType;
    public string Permission => CatalogPermissions.Brands.Create;

    public async ValueTask<TransferPreview> PreviewAsync(IAsyncEnumerable<JsonObject> rows, TransferMode mode, CancellationToken cancellationToken) =>
        (await RunAsync(rows, mode, apply: false, cancellationToken).ConfigureAwait(false)).ToPreview();

    public async ValueTask<TransferResult> ApplyAsync(IAsyncEnumerable<JsonObject> rows, TransferMode mode, CancellationToken cancellationToken) =>
        (await RunAsync(rows, mode, apply: true, cancellationToken).ConfigureAwait(false)).ToResult();

    private const int BatchSize = 1000;

    private async Task<TransferTally> RunAsync(IAsyncEnumerable<JsonObject> rows, TransferMode mode, bool apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var tally = new TransferTally(EntityType);

        // ponytail: a slug → (id, description) index of every live brand of the tenant stays in memory (rows are capped
        // by TransferOptions:MaxRowsPerFile); page it by slug if tenants reach millions of brands.
        var known = await dbContext.Brands.AsNoTracking()
            .Select(b => new { b.Slug, b.Id, b.Description })
            .ToDictionaryAsync(b => b.Slug, b => (b.Id, b.Description), StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        // Applying: pending writes, flushed every BatchSize changes so tracked entities never pile up. Preview keeps none.
        var created = new Dictionary<string, Brand>(StringComparer.Ordinal);
        var updated = new Dictionary<Guid, string?>();

        int row = 0;
        await foreach (var json in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            row++;
            string? error = Read(json, out string name, out bool hasDescription, out string? description);
            if (error is not null)
            {
                tally.Fail(row, error);
                continue;
            }

            var candidate = Brand.Create(name, description, logoUrl: null);
            if (candidate.Slug.Length == 0)
            {
                tally.Fail(row, "name must contain a letter or digit.");
                continue;
            }

            if (!known.TryGetValue(candidate.Slug, out var match))
            {
                known[candidate.Slug] = (candidate.Id, candidate.Description);
                if (apply) created[candidate.Slug] = candidate;
                tally.Create();
            }
            else if (mode == TransferMode.Upsert && hasDescription && match.Description != candidate.Description)
            {
                known[candidate.Slug] = (match.Id, candidate.Description);
                if (apply && created.TryGetValue(candidate.Slug, out var pending)) pending.Update(pending.Name, description, null);
                else if (apply) updated[match.Id] = description;
                tally.Update();
            }
            else
            {
                tally.Skip();
            }

            if (apply && created.Count + updated.Count >= BatchSize)
            {
                await FlushAsync(created, updated, cancellationToken).ConfigureAwait(false);
            }
        }

        if (apply) await FlushAsync(created, updated, cancellationToken).ConfigureAwait(false);
        return tally;
    }

    private async Task FlushAsync(Dictionary<string, Brand> created, Dictionary<Guid, string?> updated, CancellationToken cancellationToken)
    {
        if (created.Count == 0 && updated.Count == 0) return;

        if (updated.Count > 0)
        {
            var ids = updated.Keys.ToList();
            var brands = await dbContext.Brands.Where(b => ids.Contains(b.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var brand in brands)
            {
                brand.Update(brand.Name, updated[brand.Id], brand.LogoUrl);
            }
        }

        dbContext.Brands.AddRange(created.Values);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        created.Clear();
        updated.Clear();
    }

    /// <summary>Null when the row is usable; otherwise why not. Same limits as CreateBrand.</summary>
    private static string? Read(JsonObject json, out string name, out bool hasDescription, out string? description)
    {
        name = string.Empty;
        description = null;
        hasDescription = json.TryGetPropertyValue("description", out var descriptionNode);

        if (json["name"] is not JsonValue nameValue || nameValue.GetValueKind() != JsonValueKind.String) return "name must be a string.";
        name = nameValue.GetValue<string>().Trim();
        if (name.Length is 0 or > 128) return "name must be 1 to 128 characters.";

        if (descriptionNode is null) return null;
        if (descriptionNode is not JsonValue descriptionValue || descriptionValue.GetValueKind() != JsonValueKind.String)
        {
            return "description must be a string or null.";
        }

        description = descriptionValue.GetValue<string>();
        return description.Length > 1024 ? "description must be at most 1024 characters." : null;
    }
}
