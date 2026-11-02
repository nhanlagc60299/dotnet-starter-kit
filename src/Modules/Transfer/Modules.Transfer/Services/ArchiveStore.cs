using Amazon.S3;
using Amazon.S3.Model;
using FSH.Framework.Storage;
using FSH.Framework.Storage.S3;
using FSH.Framework.Storage.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Transfer.Services;

/// <summary>
/// Archives in private storage, under <c>private/tenants/{tenant}/transfer/{jobId}.zip</c>. Reads go through
/// <see cref="IStorageService"/>. <see cref="IStorageService"/> has no way to write a server-side stream to a private
/// key (its <c>UploadAsync</c> writes under <c>public/</c>, in memory, for images and documents only), so writes use the
/// provider underneath, chosen the same way <c>AddHeroStorage</c> does: the registered <see cref="IAmazonS3"/> client
/// with <c>Storage:S3</c>'s bucket and prefix, or a file under the local provider's web root (dev only: the local
/// provider does not enforce <c>private/</c>). Writes are not charged to the tenant's storage quota.
/// </summary>
public sealed class ArchiveStore(
    IServiceProvider services,
    IStorageService storage,
    IConfiguration configuration,
    IWebHostEnvironment environment)
{
    public static string KeyFor(string tenantId, Guid jobId) =>
        $"{StorageVisibilityRoot.Private}tenants/{tenantId}/transfer/{jobId:N}.zip";

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        if (string.Equals(configuration["Storage:Provider"], "s3", StringComparison.OrdinalIgnoreCase))
        {
            var options = services.GetRequiredService<IOptions<S3StorageOptions>>().Value;
            string objectKey = string.IsNullOrWhiteSpace(options.Prefix) ? key : $"{options.Prefix.TrimEnd('/')}/{key}";
            await services.GetRequiredService<IAmazonS3>().PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = objectKey,
                InputStream = content,
                ContentType = "application/zip",
                AutoCloseStream = false,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        string root = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? Path.Combine(environment.ContentRootPath, "wwwroot")
            : environment.WebRootPath;
        string path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The archive's bytes, or null when the object is gone.</summary>
    public async Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken) =>
        (await storage.DownloadAsync(key, cancellationToken).ConfigureAwait(false))?.Stream;
}
