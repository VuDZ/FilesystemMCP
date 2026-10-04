namespace FilesystemMcp;

/// <summary>
/// The single mapping from a failure to the FS-05 error object. <c>tools/call</c>
/// resolves codes here, so classification never depends on an exception message.
/// Direct RPC methods were removed in FS-12 (1.13.0): a request whose method is
/// <c>read_file</c>, <c>create_file</c>, <c>replace_in_file</c>, <c>list_directory</c>,
/// <c>search</c> or <c>append_to_file</c> is JSON-RPC <c>-32601</c> and does not run.
/// Only expected operational failures are mapped; anything else stays an unexpected
/// defect and reaches the correlation path.
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
    /// True when the failure chain contains a cancellation. The mapper treats every
    /// cancellation as the FS-05 <c>cancelled</c> outcome, but a caller that also owns
    /// an FS-07 deadline needs to tell the two causes apart before mapping: this asks
    /// the same question <see cref="TryMap"/> would, without producing an error object.
    /// </summary>
    internal static bool IsCancellation(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>
    /// An operational error that also carries why an otherwise honest partial result could
    /// not be delivered. FS-07 requires the cause to survive the transport's frame-level
    /// refusal: without this the client would see a bare <c>resource_limit</c> and lose the
    /// fact that the operation had already been cut short by a cap or by the deadline.
    /// </summary>
    internal static ToolOperationError WithTruncationReason(ToolOperationError error, string truncationReason) =>
        error.Details is { } details
            ? error with
            {
                Details = details with
                {
                    TruncationReason = truncationReason
                }
            }
            : error with
            {
                Details = new ToolErrorDetails(null, ToolErrorMessages.IsRetryable(error.Code), truncationReason)
            };

    private static ToolOperationError Create(string code, string? requested, string? specific = null) =>
        new(
            code,
            ToolErrorMessages.ForCode(code, specific),
            new ToolErrorDetails(SafeRequestedPath(requested), ToolErrorMessages.IsRetryable(code)));
}
