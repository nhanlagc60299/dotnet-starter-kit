using System.Security.Cryptography;
using FSH.Modules.Transfer.Archive;

namespace FSH.Modules.Transfer.Services;

/// <summary>Seekable scratch files for archives (zip needs random access). Deleted when disposed.</summary>
internal static class TempArchive
{
    public static FileStream Create() => new(
        Path.Combine(Path.GetTempPath(), $"fsh-transfer-{Guid.NewGuid():N}.zip"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
        FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    /// <summary>Copies at most <paramref name="maxBytes"/> (else 400), rewinds the target and returns the SHA-256 of what was copied.</summary>
    public static async Task<string> CopyAsync(Stream source, Stream target, long maxBytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) throw TransferArchive.Invalid($"The archive is larger than {maxBytes} bytes.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Seek(0, SeekOrigin.Begin);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        string sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        stream.Seek(0, SeekOrigin.Begin);
        return sha;
    }
}
