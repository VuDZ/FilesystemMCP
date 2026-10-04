namespace FilesystemMcp;

/// <summary>
/// Sink of fully formatted diagnostic lines. Implementations are called from the single
/// logger writer thread only, and are required to be best effort: a failure must never
/// escape into a tool or into the transport.
/// </summary>
internal interface ILogSink
{
    /// <summary>
    /// True once the sink can no longer accept records. The writer uses this to fall back
    /// to stderr and to report the loss exactly once instead of dropping diagnostics.
    /// </summary>
    bool IsFailed { get; }

    void Write(string line);
    void Flush();
}
