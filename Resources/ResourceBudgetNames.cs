namespace FilesystemMcp;

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
