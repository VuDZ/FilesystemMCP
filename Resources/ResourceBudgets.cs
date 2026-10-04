namespace FilesystemMcp;

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
