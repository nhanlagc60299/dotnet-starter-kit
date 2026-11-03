using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace FSH.Modules.Auditing;

internal static class HttpBodyReader
{
    public static async Task<(object? preview, int size)> ReadRequestAsync(HttpContext ctx, int maxBytes, CancellationToken ct)
    {
        if (ctx.Request.Body is null || ctx.Request.ContentLength == 0) return (null, 0);

        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        var copied = await CopyCappedAsync(ctx.Request.Body, ms, maxBytes, ct);
        ctx.Request.Body.Position = 0;

        return DeserializePreview(ms, copied);
    }

    public static async Task<(object? preview, int size)> ReadResponseAsync(Stream source, int maxBytes, CancellationToken ct)
    {
        // source is the response-body tee stream we control
        if (source.Length == 0) return (null, 0);
        source.Position = 0;

        using var ms = new MemoryStream();
        var copied = await CopyCappedAsync(source, ms, maxBytes, ct);
        return DeserializePreview(ms, copied);
    }

    private static async Task<int> CopyCappedAsync(Stream src, Stream dst, int maxBytes, CancellationToken ct)
    {
        var buf = new byte[8 * 1024];
        int total = 0, read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            var toWrite = Math.Min(read, Math.Max(0, maxBytes - total));
            if (toWrite > 0) await dst.WriteAsync(buf.AsMemory(0, toWrite), ct);
            total += read;
            if (total >= maxBytes) break;
        }
        return total;
    }

    /// <summary>Stored in place of a body that could not be parsed, so it never reaches the audit log unmasked.</summary>
    internal const string UnparsableBodyMarker = "[omitted: body could not be parsed for masking]";

    // Reject repeated keys at parse time: ToPlain's dictionary would otherwise throw (500 on every request).
    private static readonly JsonDocumentOptions ParseOptions = new() { AllowDuplicateProperties = false };

    private static (object? preview, int size) DeserializePreview(MemoryStream ms, int totalBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(ms.ToArray(), ParseOptions);
            return (ToPlain(doc.RootElement), totalBytes);
        }
        catch (JsonException)
        {
            // Malformed, truncated (over the byte cap) or duplicate-key JSON. Masking works on the
            // parsed tree, so the raw text can't be masked — drop it rather than leak e.g. a password.
            return (UnparsableBodyMarker, totalBytes);
        }
    }

    private static object? ToPlain(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject()
                .ToDictionary(p => p.Name, p => ToPlain(p.Value)),

            JsonValueKind.Array => e.EnumerateArray()
                .Select(ToPlain)
                .ToList(),

            JsonValueKind.String => e.GetString(),

            JsonValueKind.Number => GetNumericValue(e),

            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static object GetNumericValue(JsonElement e)
    {
        if (e.TryGetInt64(out var longValue))
            return longValue;

        if (e.TryGetDouble(out var doubleValue))
            return doubleValue;

        return e.GetRawText();
    }

}