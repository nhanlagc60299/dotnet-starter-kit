using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Quota;
using FSH.Framework.Shared.Quota;
using FSH.Framework.Storage;
using FSH.Framework.Storage.S3;
using FSH.Framework.Storage.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Transfer.Services;

/// <summary>
/// Private archive storage, keyed <c>private/tenants/{tenant}/transfer/{jobId}.zip</c>.
/// <list type="bullet">
/// <item><b>S3</b> (<c>Storage:Provider=s3</c>, production): written with the registered <see cref="IAmazonS3"/> under
/// <c>Storage:S3</c>'s bucket and prefix, because <see cref="IStorageService"/> cannot write a server-side stream to a
/// private key; read back through <see cref="IStorageService"/>.</item>
/// <item><b>Local</b>: a directory under the content root (<see cref="TransferOptions.LocalDirectory"/>), never the web
/// root, which the API serves anonymously as static files.</item>
/// </list>
/// Every write is charged to the tenant's <see cref="QuotaResource.StorageBytes"/> (rolled back if the write fails), like
/// <c>QuotaMeteredStorageService</c> does for uploads. Archives are never deleted, so nothing is refunded.
/// </summary>
public sealed class ArchiveStore(
    IServiceProvider services,
    IStorageService storage,
    IQuotaService quotas,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IOptions<TransferOptions> options)
{
    public static string KeyFor(string tenantId, Guid jobId) =>
        $"{StorageVisibilityRoot.Private}tenants/{tenantId}/transfer/{jobId:N}.zip";

    private bool UseS3 => string.Equals(configuration["Storage:Provider"], "s3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stores the rest of <paramref name="content"/> (seekable) as the job's archive and returns its key.</summary>
    public async Task<string> SaveAsync(string tenantId, Guid jobId, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(content);

        string key = KeyFor(tenantId, jobId);
        long bytes = content.Length - content.Position;
        var check = await quotas.CheckAndRecordAsync(tenantId, QuotaResource.StorageBytes, bytes, cancellationToken).ConfigureAwait(false);
        if (!check.Allowed)
        {
            throw new CustomException(
                $"Storage quota exceeded ({check.CurrentUsage}/{check.Limit} bytes).",
                (IEnumerable<string>?)null,
                HttpStatusCode.InsufficientStorage)
            {
                MessageKey = "Storage.QuotaExceeded",
                MessageArgs = [check.CurrentUsage, check.Limit],
            };
        }

        try
        {
            await WriteAsync(key, content, cancellationToken).ConfigureAwait(false);
            return key;
        }
        catch
        {
            // Roll the charge back so a failed write doesn't permanently consume quota.
            await quotas.RecordAsync(tenantId, QuotaResource.StorageBytes, -bytes, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The archive's bytes, or null when it is gone.</summary>
    public async Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (UseS3)
        {
            return (await storage.DownloadAsync(key, cancellationToken).ConfigureAwait(false))?.Stream;
        }

        string path = LocalPath(key);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous)
            : null;
    }

    private async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        if (UseS3)
        {
            var s3Options = services.GetRequiredService<IOptions<S3StorageOptions>>().Value;
            string objectKey = string.IsNullOrWhiteSpace(s3Options.Prefix) ? key : $"{s3Options.Prefix.TrimEnd('/')}/{key}";
            await services.GetRequiredService<IAmazonS3>().PutObjectAsync(new PutObjectRequest
            {
                BucketName = s3Options.Bucket,
                Key = objectKey,
                InputStream = content,
                ContentType = "application/zip",
                AutoCloseStream = false,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        string path = LocalPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The file for <paramref name="key"/> under the local directory. Fails closed if that directory is inside the web root or the key escapes it.</summary>
    internal string LocalPath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.LocalDirectory));
        string webRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? Path.Combine(environment.ContentRootPath, "wwwroot")
            : environment.WebRootPath);
        if (IsUnder(root, webRoot) || IsUnder(webRoot, root))
        {
            throw new InvalidOperationException(
                $"TransferOptions:LocalDirectory ({root}) must not overlap the web root ({webRoot}), which is served anonymously.");
        }

        string path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(path, root)) throw new InvalidOperationException($"Archive key '{key}' escapes the transfer directory.");
        return path;
    }

    private static bool IsUnder(string path, string directory)
    {
        string dir = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }
}
