using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FSH.Modules.Transfer.Archive;

/// <summary><c>manifest.json</c>: the format, when it was written, and one entry per <c>data/{entityType}.ndjson</c> file.</summary>
public sealed record TransferManifest(string Format, DateTime CreatedAtUtc, IReadOnlyList<TransferManifestFile> Files);

/// <summary>One data file: its path, entity type, number of NDJSON lines and lowercase hex SHA-256 of its bytes.</summary>
public sealed record TransferManifestFile(string Path, string EntityType, int Rows, string Sha256);

public static partial class TransferFormat
{
    public const string Name = "fsh.transfer/1";
    public const string ManifestPath = "manifest.json";

    /// <summary>Largest manifest read (about 200 bytes per file at the entry cap).</summary>
    public const int MaxManifestBytes = 1 << 20;

    public static string DataPath(string entityType) => $"data/{entityType}.ndjson";

    /// <summary>Lowercase <c>[a-z0-9]</c> words joined by <c>.</c>, <c>-</c> or <c>_</c>, at most 64 characters.</summary>
    public static bool IsValidEntityType(string? value) => value is { Length: > 0 and <= 64 } && EntityTypePattern().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityTypePattern();

    internal static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    internal static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
