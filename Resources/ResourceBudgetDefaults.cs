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
