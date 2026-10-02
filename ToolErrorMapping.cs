namespace FilesystemMcp;

/// <summary>
/// The FS-05 machine-readable code set. A code is the stable API surface; the
/// accompanying message is advisory and never has to be parsed by a client.
/// </summary>
internal static class ToolErrorCodes
{
    internal const string FileNotFound = "file_not_found";
    internal const string DirectoryNotFound = "directory_not_found";
    internal const string FileLocked = "file_locked";
    internal const string AccessDenied = "access_denied";
    internal const string PathOutsideWorkspace = "path_outside_workspace";
    internal const string SymlinkNotAllowed = "symlink_not_allowed";
    internal const string HashConflict = "hash_conflict";
    internal const string TargetNotFound = "target_not_found";
    internal const string FileExists = "file_exists";
    internal const string UnsupportedEncoding = "unsupported_encoding";
    internal const string BinaryFile = "binary_file";
    internal const string ResourceLimit = "resource_limit";
    internal const string Cancelled = "cancelled";
}

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

/// <summary>
/// Typed operational failure that is neither a mutation outcome nor a path-policy
/// decision, for example an exceeded resource limit. Carries the FS-05 machine code.
/// </summary>
internal sealed class OperationalException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Unknown tool name. FS-05 treats it as a JSON-RPC invalid-params failure, never as
/// an operational <c>isError</c> result. Derived from <see cref="ArgumentException"/>
/// so every boundary that rejects client-supplied arguments rejects this too.
/// </summary>
internal sealed class UnknownToolException(string toolName) : ArgumentException("Unknown tool: '" + toolName + "'");

/// <summary>
/// The single mapping from a failure to the FS-05 error object. Every entry point
/// (MCP <c>tools/call</c> and the legacy direct RPC methods) resolves codes here, so
/// classification never depends on an exception message and never diverges per
/// entry point. Only expected operational failures are mapped; anything else stays
/// an unexpected defect and reaches the correlation path.
/// </summary>
internal static class ToolErrorMapper
{
    /// <summary>Bound for the echoed relative path, matching the diagnostics cap.</summary>
    internal const int MaxRequestedPathLength = LogSanitizer.DefaultMaxLength;

    /// <summary>
    /// Bound for the interpolated part of a diagnostics text. The truncation marker
    /// adds a bounded suffix, so the resulting text may exceed this value slightly;
    /// the returned <c>-32602</c> message is a fixed product string and is not built
    /// from exception text at all.
    /// </summary>
    internal const int MaxMessageLength = 200;

    internal static bool TryMap(Exception? exception, string? requestedPath, out ToolOperationError error)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // Cancellation is an expected operational outcome (FS-05 `cancelled`),
                // not a defect; it is mapped from the type, never from message text.
                case OperationCanceledException:
                    error = Create(ToolErrorCodes.Cancelled, requested: requestedPath);
                    return true;
                case PathPolicyException policy:
                    error = Create(policy.Code, requested: requestedPath, specific: policy.Message);
                    return true;
                case MutationException mutation:
                    error = Create(mutation.Code, requested: requestedPath, specific: mutation.Message);
                    return true;
                case OperationalException operational:
                    error = Create(operational.Code, requested: requestedPath, specific: operational.Message);
                    return true;
            }
        }

        if (FileErrorClassifier.TryGetCode(exception, out var code))
        {
            error = Create(code, requested: requestedPath);
            return true;
        }

        error = null!;
        return false;
    }

    /// <summary>The mapped error, or null when the failure is an unexpected defect.</summary>
    internal static ToolOperationError? Map(Exception? exception, string? requestedPath = null) =>
        TryMap(exception, requestedPath, out var error) ? error : null;

    /// <summary>
    /// A correlation id for an unexpected defect. It is returned to the client and
    /// written to the best-effort log, so a report can be tied to one log record.
    /// </summary>
    internal static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Bounded argument-failure detail for diagnostics **only**. The client-visible
    /// <c>-32602</c> message is a fixed product string, and even the log gets no platform
    /// message: .NET argument text is not owned by this server and can embed client
    /// content (for example the regular expression behind a failed pattern compile), so
    /// only the exception type, HResult and stack trace are reported.
    /// </summary>
    internal static string ArgumentFailureDetail(Exception exception) =>
        Logging.DescribeException(exception);

    /// <summary>
    /// Echoes the requested path only when it is relative and bounded: an absolute
    /// path (even a workspace-internal one) is dropped, so an external absolute path
    /// can never appear in <c>details</c>.
    /// </summary>
    internal static string? SafeRequestedPath(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || Path.IsPathRooted(requested))
        {
            return null;
        }

        var safe = LogSanitizer.SanitizeText(requested, MaxRequestedPathLength);
        return safe.Length == 0 ? null : safe;
    }

    private static ToolOperationError Create(string code, string? requested, string? specific = null) =>
        new(
            code,
            ToolErrorMessages.ForCode(code, specific),
            new ToolErrorDetails(SafeRequestedPath(requested), ToolErrorMessages.IsRetryable(code)));
}
