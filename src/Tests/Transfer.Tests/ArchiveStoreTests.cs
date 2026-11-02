using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Quota;
using FSH.Framework.Shared.Quota;
using FSH.Modules.Transfer;
using FSH.Modules.Transfer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Transfer.Tests;

/// <summary>Local provider placement (never the web root) and storage-quota metering of <see cref="ArchiveStore"/>.</summary>
public sealed class ArchiveStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fsh-transfer-tests-{Guid.NewGuid():N}");
    private readonly FakeQuotas _quotas = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_Should_WriteOutsideTheWebRoot_ChargeQuota_And_ReadBack()
    {
        var store = Store();
        var jobId = Guid.NewGuid();
        using var content = new MemoryStream([1, 2, 3, 4, 5]);

        string key = await store.SaveAsync("t1", jobId, content, CancellationToken.None);

        key.ShouldBe($"private/tenants/t1/transfer/{jobId:N}.zip");
        File.Exists(Path.Combine(_root, "App_Data", "transfer", "private", "tenants", "t1", "transfer", $"{jobId:N}.zip")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "wwwroot", "private")).ShouldBeFalse();
        _quotas.Calls.ShouldBe([("t1", 5L)]);

        await using var read = await store.OpenAsync(key, CancellationToken.None);
        read.ShouldNotBeNull();
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task SaveAsync_Should_Return507_And_WriteNothing_When_QuotaExceeded()
    {
        _quotas.Allow = false;
        var store = Store();

        var ex = await Should.ThrowAsync<CustomException>(() =>
            store.SaveAsync("t1", Guid.NewGuid(), new MemoryStream([1, 2, 3]), CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.InsufficientStorage);
        Directory.Exists(Path.Combine(_root, "App_Data")).ShouldBeFalse();
    }

    [Fact]
    public async Task SaveAsync_Should_RefundQuota_When_WriteFails()
    {
        var store = Store();
        var jobId = Guid.NewGuid();
        await store.SaveAsync("t1", jobId, new MemoryStream([1, 2, 3]), CancellationToken.None);

        // Same key again: the file already exists, so the write fails after the charge.
        await Should.ThrowAsync<IOException>(() => store.SaveAsync("t1", jobId, new MemoryStream([1, 2, 3]), CancellationToken.None));

        _quotas.Calls.ShouldBe([("t1", 3L), ("t1", 3L), ("t1", -3L)]);
    }

    [Theory]
    [InlineData("wwwroot/transfer")]
    [InlineData(".")]
    public void LocalPath_Should_Refuse_A_Directory_Overlapping_The_WebRoot(string localDirectory)
    {
        var store = Store(localDirectory);
        Should.Throw<InvalidOperationException>(() => store.LocalPath("private/tenants/t1/transfer/x.zip"));
    }

    [Fact]
    public void LocalPath_Should_Refuse_Keys_Escaping_The_Directory()
    {
        Should.Throw<InvalidOperationException>(() => Store().LocalPath("../../outside.zip"));
    }

    private ArchiveStore Store(string localDirectory = "App_Data/transfer")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "local" })
            .Build();
        var environment = new FakeEnvironment { ContentRootPath = _root, WebRootPath = Path.Combine(_root, "wwwroot") };
        var options = Options.Create(new TransferOptions { LocalDirectory = localDirectory });
        // The local provider never touches IStorageService or the service provider (S3 only).
        return new ArchiveStore(null!, null!, _quotas, configuration, environment, options);
    }

    private sealed class FakeQuotas : IQuotaService
    {
        public bool Allow { get; set; } = true;
        public List<(string Tenant, long Amount)> Calls { get; } = [];

        public ValueTask<QuotaCheckResult> CheckAndRecordAsync(string tenantId, QuotaResource resource, long amount, CancellationToken ct = default)
        {
            resource.ShouldBe(QuotaResource.StorageBytes);
            if (Allow) Calls.Add((tenantId, amount));
            return ValueTask.FromResult(new QuotaCheckResult(Allow, resource, 10, 10, null));
        }

        public ValueTask<long> RecordAsync(string tenantId, QuotaResource resource, long amount, CancellationToken ct = default)
        {
            Calls.Add((tenantId, amount));
            return ValueTask.FromResult(0L);
        }

        public ValueTask<QuotaCheckResult> CheckAsync(string tenantId, QuotaResource resource, long amount, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<long> GetCurrentAsync(string tenantId, QuotaResource resource, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = default!;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = default!;
        public string EnvironmentName { get; set; } = "Development";
    }
}
