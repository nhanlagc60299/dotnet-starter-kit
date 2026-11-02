using System.Text;
using FSH.Framework.Core.Domain;
using FSH.Modules.Search.Contracts.Text;
using NpgsqlTypes;

namespace FSH.Modules.Search.Domain;

/// <summary>
/// The index entry for one entity of another module, unique per (tenant, <see cref="EntityType"/>,
/// <see cref="EntityId"/>). It is derived data: the owning module keeps the real record and re-indexes
/// or removes the entry, so the entry is hard-deleted, never soft-deleted.
/// </summary>
public sealed class SearchDocument : BaseEntity<Guid>
{
    public string EntityType { get; private set; } = default!;
    public string EntityId { get; private set; } = default!;
    public string Title { get; private set; } = default!;
    public string? Summary { get; private set; }
    public string? Body { get; private set; }
    public string? Url { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Weighted tsvector (title A, summary B, body C), a STORED generated column maintained by PostgreSQL.</summary>
    public NpgsqlTsVector Tsv { get; private set; } = default!;

    private SearchDocument() { }

    public static SearchDocument Create(string entityType, string entityId, string title, string? summary, string? body, string? url)
    {
        var document = new SearchDocument
        {
            Id = Guid.NewGuid(),
            EntityType = entityType,
            EntityId = entityId,
        };
        document.Update(title, summary, body, url);
        return document;
    }

    public void Update(string title, string? summary, string? body, string? url)
    {
        // NFC so composed and decomposed Vietnamese input index identically.
        Title = Nfc(title)!;
        Summary = Nfc(summary);
        Body = Nfc(body);
        Url = url;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    private static string? Nfc(string? text) => text?.SafeNormalize(NormalizationForm.FormC);
}
