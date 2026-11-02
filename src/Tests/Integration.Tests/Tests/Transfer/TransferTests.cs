using System.IO.Compression;
using System.Text;
using FSH.Modules.Transfer.Contracts.Dtos;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Transfer;

/// <summary>
/// Export → download → upload into another tenant → preview → apply, against real PostgreSQL, RustFS and Hangfire, with
/// Catalog brands as the source and sink. Plus: broken archives are refused with 400 before anything is stored.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class TransferTests
{
    private const string Brands = "catalog.brands";
    private readonly AuthHelper _auth;

    public TransferTests(FshWebApplicationFactory factory)
    {
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task ExportThenImportIntoAnotherTenant_Should_PreviewThenUpsertBrands()
    {
        string unique = Guid.NewGuid().ToString("N")[..8];
        using var root = await _auth.CreateRootAdminClientAsync();
        string alpha = $"Transfer Alpha {unique}";
        string beta = $"Transfer Beta {unique}";
        await CreateBrandAsync(root, alpha, "alpha description");
        await CreateBrandAsync(root, beta, null);

        // Entity types: brands are registered by Catalog and the admin may export and import them.
        using var typesResponse = await root.GetAsync($"{TestConstants.TransferBasePath}/entity-types");
        var types = await typesResponse.DeserializeAsync<List<TransferEntityTypeDto>>();
        types.ShouldContain(new TransferEntityTypeDto(Brands, CanExport: true, CanImport: true));

        // Export runs on Hangfire.
        using var exportResponse = await root.PostAsJsonAsync($"{TestConstants.TransferBasePath}/exports", new { entityTypes = new[] { Brands } });
        exportResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted, await exportResponse.Content.ReadAsStringAsync());
        var export = await WaitForJobAsync(root, (await exportResponse.DeserializeAsync<TransferJobDto>()).Id);
        export.Kind.ShouldBe(TransferJobKind.Export);
        int exported = export.Summary!.Rows[Brands];
        exported.ShouldBeGreaterThanOrEqualTo(2);

        using var download = await root.GetAsync($"{TestConstants.TransferBasePath}/exports/{export.Id}/download");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        download.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe(export.FileName);
        byte[] archive = await download.Content.ReadAsByteArrayAsync();
        archive.LongLength.ShouldBe(export.SizeBytes);

        // Tenant B already has Alpha (same name, other case and description), so Upsert updates it and creates the rest.
        using var other = await ProvisionTenantClientAsync(root, $"transfer-{unique}");
        await CreateBrandAsync(other, alpha.ToUpperInvariant(), "old description");

        var upload = await UploadAsync(other, archive, "brands export.zip");
        upload.Status.ShouldBe(TransferJobStatus.Pending);
        upload.EntityTypes.ShouldBe([Brands]);
        upload.FileName.ShouldBe("brands_export.zip");
        upload.Summary!.Rows[Brands].ShouldBe(exported);

        var createOnly = await PreviewAsync(other, upload.Id, "CreateOnly");
        (createOnly.Created, createOnly.Updated, createOnly.Skipped, createOnly.Failed).ShouldBe((exported - 1, 0, 1, 0));

        var preview = await PreviewAsync(other, upload.Id, "Upsert");
        (preview.Created, preview.Updated, preview.Skipped, preview.Failed).ShouldBe((exported - 1, 1, 0, 0));
        (await FindBrandAsync(other, beta)).ShouldBeNull("preview must not write");

        using var applyResponse = await other.PostAsJsonAsync($"{TestConstants.TransferBasePath}/imports/{upload.Id}/apply", new { mode = "Upsert" });
        applyResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted, await applyResponse.Content.ReadAsStringAsync());
        var applied = await WaitForJobAsync(other, upload.Id);
        applied.Mode.ShouldBe(FSH.Modules.Transfer.Contracts.TransferMode.Upsert);
        (applied.Summary!.Created, applied.Summary.Updated, applied.Summary.Failed).ShouldBe((exported - 1, 1, 0));

        var importedAlpha = await FindBrandAsync(other, alpha);
        importedAlpha.ShouldNotBeNull();
        importedAlpha.Name.ShouldBe(alpha.ToUpperInvariant(), "names of matched brands are kept");
        importedAlpha.Description.ShouldBe("alpha description");
        var importedBeta = await FindBrandAsync(other, beta);
        importedBeta.ShouldNotBeNull();
        importedBeta.Description.ShouldBeNull();

        // A succeeded import cannot be applied twice.
        using var again = await other.PostAsJsonAsync($"{TestConstants.TransferBasePath}/imports/{upload.Id}/apply", new { mode = "Upsert" });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Jobs are tenant-isolated: the export is invisible to tenant B.
        using var crossTenant = await other.GetAsync($"{TestConstants.TransferBasePath}/jobs/{export.Id}");
        crossTenant.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Apply_Should_SaveInBatches_And_UpdateABrandCreatedInAnEarlierBatch()
    {
        // 1,001 new brands (two save batches), then a row upserting the first one, which the first batch already saved.
        string unique = Guid.NewGuid().ToString("N")[..8];
        var lines = new StringBuilder();
        for (int i = 0; i < 1001; i++)
        {
            lines.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""{"name":"Batch {{unique}} {{i:D4}}","description":"v1"}""").Append('\n');
        }

        lines.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""{"name":"Batch {{unique}} 0000","description":"v2"}""").Append('\n');
        byte[] data = Encoding.UTF8.GetBytes(lines.ToString());
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
        byte[] manifest = Encoding.UTF8.GetBytes($$"""
            {"format":"fsh.transfer/1","createdAtUtc":"2026-11-02T00:00:00Z",
             "files":[{"path":"data/{{Brands}}.ndjson","entityType":"{{Brands}}","rows":1002,"sha256":"{{sha}}"}]}
            """);

        using var root = await _auth.CreateRootAdminClientAsync();
        var upload = await UploadAsync(root, Zip(("manifest.json", manifest), ($"data/{Brands}.ndjson", data)), "batch.zip");
        using var applyResponse = await root.PostAsJsonAsync($"{TestConstants.TransferBasePath}/imports/{upload.Id}/apply", new { mode = "Upsert" });
        applyResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var applied = await WaitForJobAsync(root, upload.Id);

        (applied.Summary!.Created, applied.Summary.Updated, applied.Summary.Failed).ShouldBe((1001, 1, 0));
        (await FindBrandAsync(root, $"Batch {unique} 0000"))!.Description.ShouldBe("v2");
        (await FindBrandAsync(root, $"Batch {unique} 1000"))!.Description.ShouldBe("v1");
    }

    [Fact]
    public async Task Upload_Should_Return400_When_ArchiveIsCorrupt()
    {
        using var root = await _auth.CreateRootAdminClientAsync();

        using var notZip = await PostFileAsync(root, Encoding.UTF8.GetBytes("this is not a zip archive at all"), "x.zip");
        notZip.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A real zip whose data does not match the manifest's SHA-256.
        using var tampered = await PostFileAsync(root, TamperedArchive(), "tampered.zip");
        tampered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tampered.Content.ReadAsStringAsync()).ShouldContain("SHA-256");

        using var slip = await PostFileAsync(root, Zip(("../evil.ndjson", "{}\n"u8.ToArray())), "slip.zip");
        slip.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preview_Should_Return400_When_ModeIsMissing()
    {
        using var root = await _auth.CreateRootAdminClientAsync();
        using var response = await root.PostAsJsonAsync($"{TestConstants.TransferBasePath}/imports/{Guid.NewGuid()}/preview", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static async Task CreateBrandAsync(HttpClient client, string name, string? description)
    {
        using var response = await client.PostAsJsonAsync($"{TestConstants.CatalogBasePath}/brands", new { name, description, logoUrl = (string?)null });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<BrandDto?> FindBrandAsync(HttpClient client, string name)
    {
        using var response = await client.GetAsync($"{TestConstants.CatalogBasePath}/brands?search={Uri.EscapeDataString(name)}&pageNumber=1&pageSize=50");
        var page = await response.DeserializeAsync<PagedResult<BrandDto>>();
        return page.Items.SingleOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<TransferJobDto> WaitForJobAsync(HttpClient client, Guid id)
    {
        for (int i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync($"{TestConstants.TransferBasePath}/jobs/{id}");
            var job = await response.DeserializeAsync<TransferJobDto>();
            if (job.Status == TransferJobStatus.Succeeded) return job;
            job.Status.ShouldNotBe(TransferJobStatus.Failed, job.Error);
            await Task.Delay(500);
        }

        throw new TimeoutException($"Transfer job {id} did not finish.");
    }

    private static async Task<TransferJobDto> UploadAsync(HttpClient client, byte[] archive, string fileName)
    {
        using var response = await PostFileAsync(client, archive, fileName);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<TransferJobDto>();
    }

    private static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, byte[] archive, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", fileName);
        return await client.PostAsync($"{TestConstants.TransferBasePath}/imports", form);
    }

    private static async Task<TransferSummary> PreviewAsync(HttpClient client, Guid id, string mode)
    {
        using var response = await client.PostAsJsonAsync($"{TestConstants.TransferBasePath}/imports/{id}/preview", new { mode });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<TransferSummary>();
    }

    private static byte[] TamperedArchive()
    {
        byte[] data = "{\"name\":\"Tampered\"}\n"u8.ToArray();
        byte[] manifest = Encoding.UTF8.GetBytes($$"""
            {"format":"fsh.transfer/1","createdAtUtc":"2026-11-02T00:00:00Z",
             "files":[{"path":"data/{{Brands}}.ndjson","entityType":"{{Brands}}","rows":1,"sha256":"{{new string('0', 64)}}"}]}
            """);
        return Zip(("manifest.json", manifest), ($"data/{Brands}.ndjson", data));
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(data);
            }
        }

        return stream.ToArray();
    }

    private async Task<HttpClient> ProvisionTenantClientAsync(HttpClient rootClient, string tenantId)
    {
        string adminEmail = $"{tenantId}-admin@tenant.com";
        using (var response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        }))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }

        await TenantProvisioningWait.WaitForProvisioningAsync(rootClient, tenantId);
        for (int i = 0; i < 29; i++)
        {
            try
            {
                return await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(1000);
            }
        }

        return await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);
    }
}
