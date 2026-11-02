using System.Diagnostics.CodeAnalysis;

namespace FSH.Modules.Transfer.Contracts;

/// <summary>How an import treats rows that match an existing one. There is deliberately no mirror/delete mode.</summary>
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "0 is left undefined so a request without a mode fails validation instead of picking one.")]
public enum TransferMode
{
    /// <summary>Create rows that do not exist yet; skip the ones that do.</summary>
    CreateOnly = 1,

    /// <summary>Create rows that do not exist yet; update the ones that do.</summary>
    Upsert = 2,
}
