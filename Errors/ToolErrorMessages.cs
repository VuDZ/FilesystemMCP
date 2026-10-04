namespace FilesystemMcp;

/// <summary>
/// One canonical message per FS-05 code, plus the bounded fallback used for the
/// FS-01 path-policy codes that are operational as well but are not part of the
/// FS-05 table. Messages are product text: an exception message that may embed a
/// host path is never rendered to a client.
/// </summary>
/// <remarks>
/// <see cref="ForCode"/> is contractually unbounded: it is safe only because every
/// producer of a typed operational error passes fixed product text. The only
/// <see cref="PathPolicyException"/> factory is <c>PathPolicy.Error</c>, and
/// <see cref="MutationException"/> / <see cref="OperationalException"/> are built from
/// literals or from this table, so no producer can inject a path, file content or
/// secret into <c>message</c>. A new producer forwarding arbitrary text must sanitize
/// it with <see cref="LogSanitizer.SanitizeText"/> first.
/// </remarks>
internal static class ToolErrorMessages
{
    private const string Fallback = "The operation could not be completed.";

    internal static string ForCode(string code, string? specific = null) => code switch
    {
        ToolErrorCodes.FileNotFound => "The requested file does not exist.",
        ToolErrorCodes.DirectoryNotFound => "A directory in the requested path does not exist.",
        // Phrased as a possibly temporary refusal so it is never confused with access_denied.
        ToolErrorCodes.FileLocked => "The file is temporarily locked by another process. This is not a permission problem; retry shortly.",
        ToolErrorCodes.AccessDenied => "Access to the file or directory is denied. This is a permission problem, not a temporary lock.",
        ToolErrorCodes.PathOutsideWorkspace => "The path resolves outside the workspace.",
        ToolErrorCodes.SymlinkNotAllowed => "Following links is disabled in this workspace.",
        // A patch must be based on the current text, so a re-read is required: no blind retry.
        ToolErrorCodes.HashConflict => "The file changed since it was read. Read the file again with read_file, then retry the patch with the new hash.",
        ToolErrorCodes.TargetNotFound => "The target snippet was not found in the file.",
        ToolErrorCodes.FileExists => "The file already exists. Use replace_in_file instead of create_file.",
        ToolErrorCodes.UnsupportedEncoding => "The file is not valid in a supported text encoding.",
        ToolErrorCodes.BinaryFile => "The file is binary and cannot be read or replaced as text.",
        // Covers both existing guards: the read line limits and the search match timeout.
        ToolErrorCodes.ResourceLimit => "The request exceeds an available resource limit. Narrow the request (a smaller line range, allow_large_read, or a simpler search pattern) instead of retrying it unchanged.",
        ToolErrorCodes.Cancelled => "The operation was cancelled.",
        _ => string.IsNullOrWhiteSpace(specific) ? Fallback : specific
    };

    /// <summary>Only a transient lock is retryable; a retry of an ambiguous write is never advised.</summary>
    internal static bool IsRetryable(string code) => code == ToolErrorCodes.FileLocked;
}
