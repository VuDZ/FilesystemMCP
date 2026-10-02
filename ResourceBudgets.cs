namespace FilesystemMcp;

/// <summary>
/// FS-07 resource contract. Every limit is a positive integer, validated at startup,
/// and immutable for the whole process lifetime: a client cannot raise a budget
/// through <c>tools/call</c>, it can only be narrowed by its own request shape
/// (a smaller line range, a simpler pattern).
/// </summary>
/// <remarks>
/// The single source of truth for defaults is <see cref="ResourceBudgetDefaults"/>:
/// the CLI parser, the option record and the tests all read the same constants, so a
/// documented default can never drift away from the enforced one.
/// </remarks>
internal static class ResourceBudgetDefaults
{
    /// <summary>UTF-8 bytes of one stdio frame.</summary>
    public const long MaxRequestBytes = 1_048_576;

    /// <summary>Bytes of one text file, checked before any materialization.</summary>
    public const long MaxFileBytes = 67_108_864;

    /// <summary>UTF-16 code units of one line.</summary>
    public const int MaxLineChars = 262_144;

    /// <summary>Characters of a tool payload before framing.</summary>
    public const long MaxResponseChars = 1_048_576;

    /// <summary>Files visited by one search.</summary>
    public const long SearchMaxFiles = 100_000;

    /// <summary>Directories visited by one search.</summary>
    public const long SearchMaxDirectories = 10_000;

    /// <summary>Deadline of one tool invocation.</summary>
    public const long OperationTimeoutMs = 30_000;

    /// <summary>Concurrent read/search/list operations.</summary>
    public const int MaxConcurrentReads = 4;
}

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

/// <summary>Exact CLI option names, in one place so the parser and tests cannot diverge.</summary>
internal static class ResourceBudgetNames
{
    public const string MaxRequestBytes = "maxRequestBytes";
    public const string MaxFileBytes = "maxFileBytes";
    public const string MaxLineChars = "maxLineChars";
    public const string MaxResponseChars = "maxResponseChars";
    public const string SearchMaxFiles = "searchMaxFiles";
    public const string SearchMaxDirectories = "searchMaxDirectories";
    public const string OperationTimeoutMs = "operationTimeoutMs";
    public const string MaxConcurrentReads = "maxConcurrentReads";
}

/// <summary>
/// Raised when an operation exceeds a budget that is checked before any expensive
/// materialization. It is the same FS-05 <c>resource_limit</c> outcome the existing
/// read guards produce, so a client sees one code for every budget refusal.
/// </summary>
internal sealed class ResourceLimitException(string message) : OperationalException(ToolErrorCodes.ResourceLimit, message);

/// <summary>
/// The single producer of FS-07 refusals, so every budget reports the same code and
/// the same "narrow the request" advice instead of a per-component message.
/// </summary>
internal static class ResourceBudgets
{
    /// <summary>Refusal raised when a bounded reader would have to materialize an oversized value.</summary>
    public static ResourceLimitException Limit(string detail) =>
        new(ToolErrorMessages.ForCode(ToolErrorCodes.ResourceLimit) + " (" + detail + ")");

    /// <summary>
    /// Pre-materialization file cap. The range a client asked for never bypasses this
    /// check: the content hash covers the whole file, so the whole file must be read
    /// even when one line is returned.
    /// </summary>
    public static void EnsureFileLengthWithinBudget(long fileLength, ResourceBudget budget)
    {
        if (fileLength > budget.MaxFileBytes)
        {
            throw Limit("file is larger than maxFileBytes");
        }
    }

    /// <summary>File cap for a stream that already reports its length, or 0 when unknown.</summary>
    public static void EnsureStreamWithinBudget(Stream stream, ResourceBudget budget)
    {
        if (stream.CanSeek)
        {
            EnsureFileLengthWithinBudget(stream.Length, budget);
        }
    }
}
