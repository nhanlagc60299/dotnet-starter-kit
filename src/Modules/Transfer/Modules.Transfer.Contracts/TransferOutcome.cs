namespace FSH.Modules.Transfer.Contracts;

/// <summary>A row a sink could not import. <paramref name="Row"/> is 1-based within its entity type.</summary>
public sealed record TransferRowError(string EntityType, int Row, string Message);

/// <summary>What an import would do (counts are per row).</summary>
public sealed record TransferPreview(int Created, int Updated, int Skipped, int Failed, IReadOnlyList<TransferRowError> Errors);

/// <summary>What an import did (counts are per row).</summary>
public sealed record TransferResult(int Created, int Updated, int Skipped, int Failed, IReadOnlyList<TransferRowError> Errors);

/// <summary>
/// Counts rows for a sink and keeps the first <see cref="MaxErrors"/> errors. Not thread-safe; one per import run.
/// </summary>
public sealed class TransferTally(string entityType)
{
    public const int MaxErrors = 100;

    private readonly List<TransferRowError> _errors = [];

    public int Created { get; private set; }
    public int Updated { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }

    public void Create() => Created++;
    public void Update() => Updated++;
    public void Skip() => Skipped++;

    public void Fail(int row, string message)
    {
        Failed++;
        if (_errors.Count < MaxErrors) _errors.Add(new TransferRowError(entityType, row, message));
    }

    public TransferPreview ToPreview() => new(Created, Updated, Skipped, Failed, _errors.ToArray());
    public TransferResult ToResult() => new(Created, Updated, Skipped, Failed, _errors.ToArray());
}
