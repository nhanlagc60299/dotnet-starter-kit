using System.ComponentModel.DataAnnotations;

namespace FSH.Modules.Transfer;

/// <summary>Safety limits for archives, bound from the <c>TransferOptions</c> section.</summary>
public sealed class TransferOptions
{
    /// <summary>Most entries a zip may have (checked from its end record before the directory is read).</summary>
    [Range(2, 100_000)]
    public int MaxEntries { get; set; } = 1000;

    /// <summary>Largest total uncompressed size, counted from the bytes actually inflated (zip bombs).</summary>
    [Range(typeof(long), "1024", "1099511627776")]
    public long MaxUncompressedBytes { get; set; } = 1L << 30;

    /// <summary>Largest uncompressed:compressed ratio of an entry over 1 MiB (zip bombs).</summary>
    [Range(1, 10_000)]
    public int MaxCompressionRatio { get; set; } = 100;

    /// <summary>Longest NDJSON line, in bytes. A longer line rejects the whole archive.</summary>
    [Range(1024, 64 * 1024 * 1024)]
    public int MaxLineBytes { get; set; } = 1 << 20;

    /// <summary>Most rows (NDJSON lines) per entity type. Caps what one import or preview processes.</summary>
    [Range(1, 10_000_000)]
    public int MaxRowsPerFile { get; set; } = 100_000;

    /// <summary>
    /// Local storage provider only: where archives are kept, relative to the content root (or absolute). Must not
    /// overlap the web root, which is served anonymously; production uses S3.
    /// </summary>
    [Required]
    public string LocalDirectory { get; set; } = "App_Data/transfer";

    /// <summary>Largest archive accepted by <c>POST /imports</c>.</summary>
    [Range(typeof(long), "1024", "1099511627776")]
    public long MaxUploadBytes { get; set; } = 200L << 20;
}
