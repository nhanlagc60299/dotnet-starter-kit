using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FSH.Modules.Transfer.Archive;

/// <summary>Writes an <c>fsh.transfer/1</c> zip: one NDJSON file per entity type, then <c>manifest.json</c> with each file's rows and SHA-256.</summary>
public static class TransferArchiveWriter
{
    private static readonly byte[] Newline = "\n"u8.ToArray();

    public static async Task<TransferManifest> WriteAsync(
        Stream output,
        IReadOnlyList<(string EntityType, IAsyncEnumerable<JsonObject> Rows)> sources,
        DateTime createdAtUtc,
        int maxLineBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sources);

        var files = new List<TransferManifestFile>(sources.Count);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (entityType, rows) in sources)
            {
                if (!TransferFormat.IsValidEntityType(entityType))
                {
                    throw new InvalidOperationException($"Invalid entity type '{entityType}'.");
                }

                string path = TransferFormat.DataPath(entityType);
                if (files.Exists(f => f.Path == path))
                {
                    throw new InvalidOperationException($"Entity type '{entityType}' is listed twice.");
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int count = 0;
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        byte[] line = JsonSerializer.SerializeToUtf8Bytes(row, TransferFormat.LineJson);
                        if (line.Length > maxLineBytes)
                        {
                            throw new InvalidOperationException(
                                $"Row {count + 1} of '{entityType}' is longer than {maxLineBytes} bytes.");
                        }

                        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                        await stream.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
                        hash.AppendData(line);
                        hash.AppendData(Newline);
                        count++;
                    }
                }

                files.Add(new TransferManifestFile(path, entityType, count, Convert.ToHexStringLower(hash.GetHashAndReset())));
            }

            var manifest = new TransferManifest(TransferFormat.Name, createdAtUtc, files);
            var manifestStream = await zip.CreateEntry(TransferFormat.ManifestPath).OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (manifestStream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(manifestStream, manifest, TransferFormat.ManifestJson, cancellationToken)
                    .ConfigureAwait(false);
            }

            return manifest;
        }
    }
}
