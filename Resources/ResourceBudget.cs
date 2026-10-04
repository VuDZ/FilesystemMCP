namespace FilesystemMcp;

/// <summary>
/// The immutable, validated FS-07 budget set. Separate from <see cref="ServerOptions"/>
/// so components that must not depend on startup concerns (streaming readers, the
/// search tool) receive exactly the numbers they enforce. Defaults come from
/// <see cref="ResourceBudgetDefaults"/>.
/// </summary>
internal sealed record ResourceBudget
{
    public long MaxRequestBytes { get; init; } = ResourceBudgetDefaults.MaxRequestBytes;
    public long MaxFileBytes { get; init; } = ResourceBudgetDefaults.MaxFileBytes;
    public int MaxLineChars { get; init; } = ResourceBudgetDefaults.MaxLineChars;
    public long MaxResponseChars { get; init; } = ResourceBudgetDefaults.MaxResponseChars;
    public long SearchMaxFiles { get; init; } = ResourceBudgetDefaults.SearchMaxFiles;
    public long SearchMaxDirectories { get; init; } = ResourceBudgetDefaults.SearchMaxDirectories;
    public long OperationTimeoutMs { get; init; } = ResourceBudgetDefaults.OperationTimeoutMs;
    public int MaxConcurrentReads { get; init; } = ResourceBudgetDefaults.MaxConcurrentReads;

    public static ResourceBudget Default { get; } = new();

    /// <summary>
    /// The largest accepted <c>operationTimeoutMs</c>: <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// is implemented with a 32-bit millisecond timer and rejects anything above this
    /// value. A larger timeout is therefore rejected at startup with every other invalid
    /// budget, instead of failing later on a live request — where the failure would be
    /// invisible to the client, because the reply is built after the deadline is armed.
    /// </summary>
    public const long MaxOperationTimeoutMs = 4_294_967_294;

    /// <summary>
    /// Fails at startup when any limit is non-positive. A zero or negative budget is
    /// never a legitimate configuration: it would turn every request into a refusal
    /// instead of narrowing one, so it is a startup error like any other CLI mistake.
    /// </summary>
    public void Validate()
    {
        RequirePositive(MaxRequestBytes, ResourceBudgetNames.MaxRequestBytes);
        RequirePositive(MaxFileBytes, ResourceBudgetNames.MaxFileBytes);
        RequirePositive(MaxLineChars, ResourceBudgetNames.MaxLineChars);
        RequirePositive(MaxResponseChars, ResourceBudgetNames.MaxResponseChars);
        RequirePositive(SearchMaxFiles, ResourceBudgetNames.SearchMaxFiles);
        RequirePositive(SearchMaxDirectories, ResourceBudgetNames.SearchMaxDirectories);
        RequirePositive(OperationTimeoutMs, ResourceBudgetNames.OperationTimeoutMs);
        RequirePositive(MaxConcurrentReads, ResourceBudgetNames.MaxConcurrentReads);
        if (OperationTimeoutMs > MaxOperationTimeoutMs)
        {
            throw new ArgumentException(
                ResourceBudgetNames.OperationTimeoutMs + " must be at most " + MaxOperationTimeoutMs + ".");
        }
    }

    private static void RequirePositive(long value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentException(name + " must be a positive integer.");
        }
    }
}
