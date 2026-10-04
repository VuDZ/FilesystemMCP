namespace FilesystemMcp;

/// <summary>
/// The operation deadline of the request that is currently executing, published to the
/// tool that runs under it. A tool needs the distinction for one reason only: the FS-07
/// contract says a fired deadline produces a partial result, while a peer
/// <c>notifications/cancelled</c> must surface as the FS-05 <c>cancelled</c> outcome.
/// The single <see cref="CancellationToken"/> a tool receives cannot express that,
/// because the transport links both sources into it.
/// </summary>
/// <remarks>
/// This is a fact, not a heuristic: it is the very token the transport scheduled, so
/// "cancelled and this token is cancelled" is the deadline and "cancelled and this token
/// is not" is the peer. An earlier version guessed from elapsed wall-clock time and
/// misreported a late peer cancel as a successful timeout.
/// <para>
/// The value is ambient on purpose. Threading a second token through
/// <see cref="IMcpTool"/> would widen the tool interface for every tool to serve one of
/// them, and it would still not reach a tool that is invoked directly by a test. The
/// scope is set by <see cref="ToolRegistry"/> immediately before the tool body runs and
/// is restored on the same thread, so a tool never observes a deadline that belongs to a
/// different request.
/// </para>
/// </remarks>
internal static class RequestDeadline
{
    /// <summary>The deadline token of the running request, or null outside a request.</summary>
    internal static CancellationToken? Token => _current.Value;

    /// <summary>
    /// True when this failure is the operation deadline rather than the peer. The ambient
    /// token is cancelled by the deadline timer and by nothing else, so its cancellation
    /// state is the whole answer: a token that is not cancelled means the peer cancelled
    /// (or the session ended), and a null ambient token means no deadline was scheduled
    /// for this request at all.
    /// </summary>
    /// <remarks>
    /// There is deliberately no comparison against the caller's own token. An earlier
    /// version compared them for "honesty" and was always false, because
    /// <see cref="CancellationToken"/> has no equality operator: the two values were boxed
    /// and compared by reference, so a fired deadline was reported as a peer cancel. The
    /// ambient token is never the token a tool receives — the tool's token is linked to the
    /// peer's cancellation as well — so the two are not meant to be equal.
    /// </remarks>
    internal static bool IsDeadline() => Token?.IsCancellationRequested == true;

    /// <summary>Publishes the deadline for the duration of the returned scope.</summary>
    internal static Scope Begin(CancellationToken deadlineToken) => new(_current.Value, deadlineToken);

    /// <summary>
    /// Restores the value it replaced when it is disposed. It stays nested because reading
    /// and writing the ambient value is private to <see cref="RequestDeadline"/>, and the
    /// style forbids widening that access only to move a type into its own file.
    /// </summary>
    internal readonly struct Scope : IDisposable
    {
        internal Scope(CancellationToken? previous, CancellationToken applied)
        {
            _previous = previous;
            _applied = applied;
            _current.Value = applied;
        }

        public void Dispose() => _current.Value = _previous;

        private readonly CancellationToken? _previous;
        private readonly CancellationToken _applied;
    }

    private static readonly AsyncLocal<CancellationToken?> _current = new();
}
