using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FSH.Framework.Core.Exceptions;

namespace FSH.Modules.Transfer.Archive;

/// <summary>
/// An <c>fsh.transfer/1</c> zip opened for import. <see cref="Open"/> checks everything before a row is handed out and
/// rejects the whole archive (400) on the first problem: entry count from the end record (before the directory is
/// read), entry names (no absolute, <c>..</c>, <c>.</c>, backslash, drive or control characters: zip slip), total
/// uncompressed size and compression ratio (zip bombs), a manifest of the right format listing exactly the data files
/// present, and for each file its SHA-256, row count and every line (at most <see cref="TransferOptions.MaxLineBytes"/>,
/// a JSON object). Nothing is ever extracted to disk.
/// </summary>
public sealed class TransferArchive : IDisposable
{
    private const long RatioCheckFloor = 1L << 20;

    // Duplicate keys would otherwise surface later, inside a sink, as an ArgumentException.
    private static readonly JsonDocumentOptions RowJson = new() { AllowDuplicateProperties = false };

    private readonly ZipArchive _zip;
    private readonly TransferOptions _options;

    private TransferArchive(ZipArchive zip, TransferManifest manifest, TransferOptions options)
    {
        _zip = zip;
        Manifest = manifest;
        _options = options;
    }

    public TransferManifest Manifest { get; }

    /// <summary>Validates <paramref name="stream"/> (seekable, left open) as described on the class.</summary>
    public static TransferArchive Open(Stream stream, TransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            Preflight(stream, options);
            stream.Seek(0, SeekOrigin.Begin);
            var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
            try
            {
                CheckEntries(zip, options);
                var manifest = ReadManifest(zip);
                VerifyFiles(zip, manifest, options);
                return new TransferArchive(zip, manifest, options);
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }
        catch (InvalidDataException ex)
        {
            throw Invalid($"The file is not a valid zip archive: {ex.Message}");
        }
        catch (EndOfStreamException)
        {
            throw Invalid("The file is not a valid zip archive (truncated).");
        }
    }

    /// <summary>The rows of one entity type, in file order. Lines were validated by <see cref="Open"/>.</summary>
    public IAsyncEnumerable<JsonObject> ReadRowsAsync(string entityType) => ReadRows(entityType).ToAsyncEnumerable();

    private IEnumerable<JsonObject> ReadRows(string entityType)
    {
        var file = Manifest.Files.FirstOrDefault(f => f.EntityType == entityType)
            ?? throw Invalid($"The archive has no data for '{entityType}'.");
        var entry = _zip.GetEntry(file.Path)!;
        var budget = new StrongBox<long>(_options.MaxUncompressedBytes);
        using var stream = entry.Open();
        int row = 0;
        foreach (byte[] line in ReadLines(stream, file.Path, entry.Length, _options.MaxLineBytes, budget, hash: null))
        {
            yield return ParseRow(line, file.Path, ++row);
        }
    }

    public void Dispose() => _zip.Dispose();

    internal static CustomException Invalid(string message) =>
        new(message, (IEnumerable<string>?)null, HttpStatusCode.BadRequest);

    /// <summary>
    /// Reads the end-of-central-directory record (and the Zip64 one when present) and refuses archives that announce
    /// more entries than allowed or an oversized central directory, without reading the directory itself.
    /// </summary>
    internal static void Preflight(Stream file, TransferOptions options)
    {
        long length = file.Length;
        if (length < 22) throw Invalid("The file is not a valid zip archive.");
        int tail = (int)Math.Min(length, 22 + 65535);
        var buffer = new byte[tail];
        file.Seek(length - tail, SeekOrigin.Begin);
        file.ReadExactly(buffer);

        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
        {
            if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0) throw Invalid("The file is not a valid zip archive (no central directory).");

        long entries = BitConverter.ToUInt16(buffer, eocd + 10);
        long size = BitConverter.ToUInt32(buffer, eocd + 12);

        // Zip64: the locator sits just before the end record and points to the Zip64 end record.
        if ((entries == 0xFFFF || size == 0xFFFFFFFF) && eocd >= 20 && BitConverter.ToUInt32(buffer, eocd - 20) == 0x07064b50)
        {
            ulong recordOffset = BitConverter.ToUInt64(buffer, eocd - 20 + 8);
            if (recordOffset > (ulong)(length - 56)) throw Invalid("The zip archive is corrupt (Zip64 record).");
            var record = new byte[56];
            file.Seek((long)recordOffset, SeekOrigin.Begin);
            file.ReadExactly(record);
            if (BitConverter.ToUInt32(record, 0) != 0x06064b50) throw Invalid("The zip archive is corrupt (Zip64 record).");
            entries = (long)Math.Min(BitConverter.ToUInt64(record, 32), long.MaxValue);
            size = (long)Math.Min(BitConverter.ToUInt64(record, 40), long.MaxValue);
        }

        if (entries > options.MaxEntries) throw Invalid($"The archive has more than {options.MaxEntries} entries.");

        // About 300 bytes per entry at the entry cap.
        long maxDirectory = Math.Max(1L << 20, options.MaxEntries * 320L);
        if (size > maxDirectory || size > length) throw Invalid("The central directory of the zip archive is too large.");
    }

    private static void CheckEntries(ZipArchive zip, TransferOptions options)
    {
        if (zip.Entries.Count > options.MaxEntries) throw Invalid($"The archive has more than {options.MaxEntries} entries.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            if (!IsSafeEntryName(name)) throw Invalid($"Entry name \"{Printable(name)}\" is not allowed (path outside the archive).");
            if (!names.Add(name)) throw Invalid($"Entry \"{name}\" appears twice.");

            total += entry.Length;
            if (total > options.MaxUncompressedBytes)
            {
                throw Invalid($"The archive inflates to more than {options.MaxUncompressedBytes} bytes.");
            }

            if (entry.Length > RatioCheckFloor && entry.Length / Math.Max(1, entry.CompressedLength) > options.MaxCompressionRatio)
            {
                throw Invalid($"Entry \"{name}\" has a suspicious compression ratio (zip bomb).");
            }
        }
    }

    internal static bool IsSafeEntryName(string name) =>
        name.Length is > 0 and <= 256
        && !name.StartsWith('/')
        && !name.Contains('\\', StringComparison.Ordinal)
        && !name.Contains(':', StringComparison.Ordinal)
        && !name.Any(char.IsControl)
        && !name.Split('/').Any(segment => segment is "" or "." or "..");

    private static string Printable(string value) =>
        new(value.Take(80).Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private static TransferManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(TransferFormat.ManifestPath)
            ?? throw Invalid("manifest.json is missing: this is not an fsh.transfer archive.");

        byte[] bytes;
        using (var stream = entry.Open())
        using (var buffer = new MemoryStream())
        {
            // Copy at most one byte past the cap: the declared length cannot be trusted.
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > TransferFormat.MaxManifestBytes) throw Invalid("manifest.json is too large.");
            }

            bytes = buffer.ToArray();
        }

        TransferManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TransferManifest>(bytes, TransferFormat.ManifestJson);
        }
        catch (JsonException ex)
        {
            throw Invalid($"manifest.json is not valid: {ex.Message}");
        }

        if (manifest is null) throw Invalid("manifest.json is empty.");
        if (manifest.Format != TransferFormat.Name) throw Invalid($"Format \"{Printable(manifest.Format)}\" is not {TransferFormat.Name}.");
        if (manifest.Files.Count == 0) throw Invalid("The archive contains no data files.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file is null) throw Invalid("manifest.json lists an empty file.");
            if (!TransferFormat.IsValidEntityType(file.EntityType))
            {
                throw Invalid($"Entity type \"{Printable(file.EntityType)}\" is not valid.");
            }

            if (file.Path != TransferFormat.DataPath(file.EntityType))
            {
                throw Invalid($"File \"{Printable(file.Path)}\" must be {TransferFormat.DataPath(file.EntityType)}.");
            }

            if (!seen.Add(file.EntityType)) throw Invalid($"Entity type \"{file.EntityType}\" is listed twice.");
            if (file.Rows < 0) throw Invalid($"File {file.Path} has a negative row count.");
            if (file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit))
            {
                throw Invalid($"File {file.Path} has no valid SHA-256.");
            }
        }

        // Fail closed: every entry is the manifest or a listed data file.
        var stray = zip.Entries.FirstOrDefault(e =>
            e.FullName != TransferFormat.ManifestPath && !manifest.Files.Any(f => f.Path == e.FullName));
        if (stray is not null) throw Invalid($"Entry \"{Printable(stray.FullName)}\" is not listed in manifest.json.");

        return manifest;
    }

    private static void VerifyFiles(ZipArchive zip, TransferManifest manifest, TransferOptions options)
    {
        var budget = new StrongBox<long>(options.MaxUncompressedBytes);
        foreach (var file in manifest.Files)
        {
            var entry = zip.GetEntry(file.Path) ?? throw Invalid($"File {file.Path} listed in manifest.json is missing.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = entry.Open();
            int rows = 0;
            foreach (byte[] line in ReadLines(stream, file.Path, entry.Length, options.MaxLineBytes, budget, hash))
            {
                ParseRow(line, file.Path, ++rows);
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid($"File {file.Path} does not match its SHA-256 in manifest.json (corrupt or modified).");
            }

            if (rows != file.Rows) throw Invalid($"File {file.Path} has {rows} rows; manifest.json says {file.Rows}.");
        }
    }

    /// <summary>
    /// Splits a stream on <c>\n</c> (a trailing <c>\r</c> is left to the JSON parser as whitespace), one line in memory
    /// at a time. Throws when a line exceeds <paramref name="maxLineBytes"/>, when more bytes inflate than the entry
    /// declared, or when the archive-wide <paramref name="budget"/> runs out.
    /// </summary>
    private static IEnumerable<byte[]> ReadLines(Stream stream, string path, long declaredLength, int maxLineBytes, StrongBox<long> budget, IncrementalHash? hash)
    {
        using var line = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int count = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            budget.Value -= read;
            if (total > declaredLength) throw Invalid($"File {path} inflates past its declared size.");
            if (budget.Value < 0) throw Invalid("The archive inflates to more than the allowed size.");
            hash?.AppendData(chunk, 0, read);

            int start = 0;
            int newline;
            while ((newline = Array.IndexOf(chunk, (byte)'\n', start, read - start)) >= 0)
            {
                Append(line, chunk, start, newline - start, maxLineBytes, path, count + 1);
                count++;
                yield return line.ToArray();
                line.SetLength(0);
                start = newline + 1;
            }

            Append(line, chunk, start, read - start, maxLineBytes, path, count + 1);
        }

        if (line.Length > 0) yield return line.ToArray();
    }

    private static void Append(MemoryStream line, byte[] chunk, int offset, int count, int maxLineBytes, string path, int row)
    {
        if (line.Length + count > maxLineBytes) throw Invalid($"Line {row} of {path} is longer than {maxLineBytes} bytes.");
        line.Write(chunk, offset, count);
    }

    private static JsonObject ParseRow(byte[] line, string path, int row)
    {
        try
        {
            return JsonNode.Parse(line, documentOptions: RowJson) as JsonObject ?? throw Invalid($"Line {row} of {path} is not a JSON object.");
        }
        catch (JsonException)
        {
            throw Invalid($"Line {row} of {path} is not valid JSON.");
        }
    }
}
