using System.Linq.Expressions;
using FSH.Framework.Jobs.Services;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Services;
using Hangfire;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Integration.Tests.Tests.Transfer;

/// <summary>
/// With the local storage provider, archives live in <c>TransferOptions:LocalDirectory</c> (outside the web root), so
/// the static-file middleware cannot serve them anonymously, while the authenticated download still works.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class TransferLocalStorageTests : IDisposable
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fsh-transfer-it-{Guid.NewGuid():N}");

    public TransferLocalStorageTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task LocalArchives_Should_NotBeServedAsStaticFiles_But_DownloadThroughTheApi()
    {
        // A second host re-runs AddHangfire, which repoints the global JobStorage.Current at its own in-memory storage
        // and disposes that storage with the host; later enqueues from the main host would then fail. Restore it.
        var mainJobStorage = JobStorage.Current;
        try
        {
            await RunAsync();
        }
        finally
        {
            JobStorage.Current = mainJobStorage;
        }
    }

    private async Task RunAsync()
    {
        await using var local = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "local",
                ["TransferOptions:LocalDirectory"] = _directory,
            }));
            // Run the export inline (below) instead of on the Hangfire server shared with the main test host.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJobService>();
                services.AddSingleton<IJobService, CapturingJobService>();
            });
        });

        var token = await _auth.GetRootAdminTokenAsync();
        using var client = local.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        using (var brand = await client.PostAsJsonAsync($"{TestConstants.CatalogBasePath}/brands",
            new { name = $"Transfer Local {Guid.NewGuid():N}", description = (string?)null, logoUrl = (string?)null }))
        {
            brand.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var exportResponse = await client.PostAsJsonAsync($"{TestConstants.TransferBasePath}/exports", new { entityTypes = new[] { "catalog.brands" } });
        var export = await exportResponse.DeserializeAsync<TransferJobDto>();
        await ActivatorUtilities.CreateInstance<TransferJobRunner>(local.Services)
            .RunExportAsync(export.Id, TestConstants.RootTenantId, CancellationToken.None);

        using var jobResponse = await client.GetAsync($"{TestConstants.TransferBasePath}/jobs/{export.Id}");
        var job = await jobResponse.DeserializeAsync<TransferJobDto>();
        job.Status.ShouldBe(TransferJobStatus.Succeeded, job.Error);

        string relative = $"private/tenants/{TestConstants.RootTenantId}/transfer/{export.Id:N}.zip";
        File.Exists(Path.Combine(_directory, relative)).ShouldBeTrue();

        using var download = await client.GetAsync($"{TestConstants.TransferBasePath}/exports/{export.Id}/download");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        byte[] archive = await download.Content.ReadAsByteArrayAsync();
        archive.LongLength.ShouldBe(job.SizeBytes);

        // An upload is stored the same way.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(archive), "file", "export.zip");
        using var uploadResponse = await client.PostAsync($"{TestConstants.TransferBasePath}/imports", form);
        var upload = await uploadResponse.DeserializeAsync<TransferJobDto>();

        // No token, but a valid tenant header, as an attacker would send.
        using var anonymous = local.CreateClient();
        anonymous.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        // Control first: the same path IS served anonymously when a file sits in the web root, so the 404s are not vacuous.
        string webRoot = local.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        string privateRoot = Path.Combine(webRoot, "private");
        bool createdPrivateRoot = !Directory.Exists(privateRoot);
        string probeName = $"probe-{Guid.NewGuid():N}.zip";
        string probe = Path.Combine(privateRoot, "tenants", TestConstants.RootTenantId, "transfer", probeName);
        Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
        await File.WriteAllBytesAsync(probe, [1, 2, 3]);
        try
        {
            using var served = await anonymous.GetAsync($"/private/tenants/{TestConstants.RootTenantId}/transfer/{probeName}");
            served.StatusCode.ShouldBe(HttpStatusCode.OK);
            foreach (Guid id in new[] { export.Id, upload.Id })
            {
                // Not a static file, so the request falls through: 401 without a token, 404 with one.
                string path = $"/private/tenants/{TestConstants.RootTenantId}/transfer/{id:N}.zip";
                using var anonymousGet = await anonymous.GetAsync(path);
                anonymousGet.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                using var authenticatedGet = await client.GetAsync(path);
                authenticatedGet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }
        }
        finally
        {
            if (createdPrivateRoot) Directory.Delete(privateRoot, recursive: true);
            else File.Delete(probe);
        }
    }

    private sealed class CapturingJobService : IJobService
    {
        private static string Captured() => Guid.NewGuid().ToString("N");

        public bool Delete(string jobId) => true;
        public bool Delete(string jobId, string fromState) => true;
        public string Enqueue(Expression<Action> methodCall) => Captured();
        public string Enqueue(string queue, Expression<Func<Task>> methodCall) => Captured();
        public string Enqueue(Expression<Func<Task>> methodCall) => Captured();
        public string Enqueue<T>(Expression<Action<T>> methodCall) => Captured();
        public string Enqueue<T>(Expression<Func<T, Task>> methodCall) => Captured();
        public bool Requeue(string jobId) => true;
        public bool Requeue(string jobId, string fromState) => true;
        public string Schedule(Expression<Action> methodCall, TimeSpan delay) => Captured();
        public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay) => Captured();
        public string Schedule(Expression<Action> methodCall, DateTimeOffset enqueueAt) => Captured();
        public string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt) => Captured();
        public string Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay) => Captured();
        public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay) => Captured();
        public string Schedule<T>(Expression<Action<T>> methodCall, DateTimeOffset enqueueAt) => Captured();
        public string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt) => Captured();
    }
}
