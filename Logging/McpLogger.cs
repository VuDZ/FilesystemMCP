using System.Text.Json;

namespace FilesystemMcp;

/// <summary>
/// Best-effort diagnostics facade. Every stage — path selection, directory creation,
/// formatting, queueing, file write, rotation and the stderr fallback — is guarded, so a
/// logging failure can never turn a committed mutation into a failure, replace the
/// original exception, or reach the transport. stdout is never written here: it belongs
/// to JSON-RPC frames.
/// </summary>
internal static class McpLogger
{
    internal const int FailureFormat = 0;
    internal const int FailureWrite = 1;
    internal const int FailureStderr = 2;

    /// <summary>
    /// Test seam: the next diagnostic record fails in exactly one stage (0=format,
    /// 1=write, 2=stderr) and the injection is then consumed, so a later record travels the
    /// same path normally.
    /// </summary>
    internal static LogFailureInjection? FailNextWrite { get; set; }

    /// <summary>Diagnostic channel. Never stdout, and replaceable so tests can make it fail.</summary>
    internal static TextWriter ErrorWriter
    {
        get
        {
            lock (_gate)
            {
                return _errorWriter;
            }
        }
        set
        {
            lock (_gate)
            {
                _errorWriter = value ?? TextWriter.Null;
            }
        }
    }

    internal static bool IsFileSinkEnabled
    {
        get
        {
            lock (_gate)
            {
                return _fileSinkEnabled;
            }
        }
    }

    internal static string? ActiveLogDirectory
    {
        get
        {
            lock (_gate)
            {
                return _activeDirectory;
            }
        }
    }

    internal static string? ActiveLogFilePath
    {
        get
        {
            lock (_gate)
            {
                return _activeFilePath;
            }
        }
    }

    internal static long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return (_queue?.DroppedCount ?? 0) + _droppedByWriter;
            }
        }
    }

    internal static void Start(string? logDirectory) => StartWith(null, logDirectory);

    /// <summary>
    /// Starts the single writer. Any failure leaves diagnostics disabled (one safe stderr
    /// message) and lets the process keep serving requests.
    /// </summary>
    internal static void StartWith(
        ILogSink? sink,
        string? logDirectory = null,
        int maxBytes = (int)LogFileSink.DefaultMaxBytes,
        int maxFilesPerSession = LogFileSink.DefaultMaxFilesPerSession,
        int maxEntries = 4096,
        long maxBytesQueued = 1024 * 1024,
        int maxEntryBytes = Logging.MaxLineChars)
    {
        try
        {
            lock (_gate)
            {
                if (_queue is not null)
                {
                    return;
                }

                var directory = Logging.ResolveDirectory(logDirectory);
                _activeDirectory = directory;
                var selected = sink;
                if (selected is null)
                {
                    if (!Logging.TryPrepareDirectory(directory, out var reason))
                    {
                        ReportSinkDisabled(directory, reason);
                        StartWriterLocked(queue: new BoundedLogQueue(maxEntries, maxBytesQueued, maxEntryBytes), sink: null);
                        return;
                    }

                    try
                    {
                        var fileSink = new LogFileSink(
                            directory,
                            Logging.SessionFileName(DateTimeOffset.UtcNow, Environment.ProcessId),
                            maxBytes,
                            maxFilesPerSession);
                        _activeFilePath = fileSink.CurrentFilePath;
                        if (fileSink.IsFailed)
                        {
                            // The directory exists but this session file is unusable:
                            // stderr takes over instead of silently losing records.
                            ReportSinkDisabled(directory, "unavailable-file");
                        }
                        else
                        {
                            selected = fileSink;
                            _fileSinkEnabled = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        ReportSinkDisabled(directory, ex.GetType().Name);
                        StartWriterLocked(queue: new BoundedLogQueue(maxEntries, maxBytesQueued, maxEntryBytes), sink: null);
                        return;
                    }
                }
                else
                {
                    _fileSinkEnabled = true;
                }

                StartWriterLocked(new BoundedLogQueue(maxEntries, maxBytesQueued, maxEntryBytes), selected);
            }
        }
        catch (Exception)
        {
            // Starting diagnostics must never prevent the server from starting.
        }
    }

    internal static void Info(string message, string? correlationId = null) =>
        Submit(Logging.InfoLevel, message, correlationId, hooked: true);

    internal static void Error(string message, Exception? exception = null, string? correlationId = null)
    {
        try
        {
            var text = Logging.DescribeException(exception);
            if (text.Length > 0)
            {
                message = message + Environment.NewLine + text;
            }
        }
        catch (Exception)
        {
            // Describing the exception is part of the best-effort pipeline too.
        }

        Submit(Logging.ErrorLevel, message, correlationId, hooked: true);
    }

    internal static void ToolInvoke(string toolName, JsonElement arguments, string? correlationId = null)
    {
        string safeArguments;
        try
        {
            safeArguments = LogSanitizer.SanitizeForLog(arguments);
        }
        catch (Exception)
        {
            safeArguments = "<unavailable>";
        }

        Submit(Logging.InfoLevel, $"Tool invoke: {toolName} args={safeArguments}", correlationId, hooked: false);
    }

    internal static void ToolComplete(string toolName, TimeSpan elapsed, bool success, string? detail = null, string? correlationId = null)
    {
        var status = success ? "ok" : "failed";
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
        Submit(Logging.InfoLevel, $"Tool done: {toolName} status={status} elapsedMs={elapsed.TotalMilliseconds:F0}{suffix}", correlationId, hooked: false);
    }

    /// <summary>
    /// Bounded exit flush: accepts no new records, lets the writer drain until the
    /// deadline, closes the sink and returns whether everything was written. Never waits
    /// longer than <paramref name="timeout"/> and never throws.
    /// </summary>
    internal static bool Shutdown(TimeSpan timeout)
    {
        Thread? writer;
        BoundedLogQueue? queue;
        try
        {
            lock (_gate)
            {
                writer = _writer;
                queue = _queue;
                if (writer is null || queue is null)
                {
                    return true;
                }

                _shutdownRequested = true;
            }

            // The state lock is released before waiting: the writer and the request path
            // both take it, and the bounded contract covers the wait, not only the drain.
            try
            {
                Monitor.PulseAll(_gate);
            }
            catch (Exception)
            {
                // A missed pulse only costs one wait timeout.
            }

            if (!writer.Join(timeout))
            {
                // The writer is stuck in a hostile sink; do not extend the exit budget.
                try
                {
                    writer.Interrupt();
                }
                catch (Exception)
                {
                    // Interrupting is advisory; the thread is a background thread anyway.
                }

                return false;
            }

            return queue.PendingCount == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Test seam only: full stop plus a reusable clean state, so a second in-process
    /// scenario does not inherit the first one's sink, queue or counters.
    /// </summary>
    internal static void ResetForTests()
    {
        try
        {
            Shutdown(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // A stuck writer must not block the reset.
        }

        lock (_gate)
        {
            _queue = null;
            _sink = null;
            _writer = null;
            _shutdownRequested = false;
            _fileSinkEnabled = false;
            _activeDirectory = null;
            _activeFilePath = null;
            _errorWriter = Console.Error;
            FailNextWrite = null;
            _droppedByWriter = 0;
        }
    }

    private static void StartWriterLocked(BoundedLogQueue queue, ILogSink? sink)
    {
        _queue = queue;
        _sink = sink;
        _writer = new Thread(() => WriterLoop(queue, sink))
        {
            IsBackground = true,
            Name = "filesystemmcp-log-writer"
        };
        _writer.Start();
    }

    /// <summary>
    /// The single writer. Every iteration is guarded as a whole: the writer is the only
    /// channel diagnostics have, so nothing may escape and kill it. Records are routed per
    /// line — to the file sink while it is healthy, otherwise to stderr — so no request
    /// path ever performs stderr I/O itself.
    /// </summary>
    private static void WriterLoop(BoundedLogQueue queue, ILogSink? sink)
    {
        while (true)
        {
            string? line = null;
            var stopping = false;
            try
            {
                lock (_gate)
                {
                    if (queue.PendingCount == 0)
                    {
                        if (_shutdownRequested)
                        {
                            stopping = true;
                        }
                        else
                        {
                            Monitor.Wait(_gate, 200);
                        }
                    }
                }

                if (!stopping && queue.TryDequeue(out var dequeued))
                {
                    line = dequeued;
                }

                if (line is not null)
                {
                    WriteRecord(IsFileSinkActive() ? sink : null, line);
                }
            }
            catch (Exception)
            {
                // Nothing may escape this thread: a dead writer would silence diagnostics
                // permanently. Short backoff keeps a permanently failing queue from
                // spinning the CPU.
                Thread.Sleep(50);
            }

            if (stopping)
            {
                break;
            }
        }

        try
        {
            // Bounded flush of everything accepted before the shutdown flag.
            var remainder = new List<string>();
            queue.DrainRemaining(remainder, DateTimeOffset.UtcNow.AddSeconds(1));
            foreach (var pending in remainder)
            {
                WriteRecord(IsFileSinkActive() ? sink : null, pending);
            }

            if (sink is not null)
            {
                var failedBeforeFlush = sink.IsFailed;
                sink.Flush();
                if (!failedBeforeFlush && sink.IsFailed)
                {
                    // Flush swallowed the failure. Same outcome as a failed write: the
                    // record is counted and the sink is retired, once.
                    Interlocked.Increment(ref _droppedByWriter);
                    AbandonSink();
                }
            }

            var dropped = queue.DroppedCount + Interlocked.Read(ref _droppedByWriter);
            if (dropped > 0)
            {
                WriteRecord(IsFileSinkActive() ? sink : null, Logging.Format(new LogEntry(
                    DateTimeOffset.UtcNow,
                    Logging.InfoLevel,
                    $"Dropped {dropped} diagnostic record(s): queue bounded or sink unavailable.",
                    null)));
            }
        }
        catch (Exception)
        {
            // Final flush is best effort by contract.
        }

        try
        {
            if (sink is LogFileSink fileSink)
            {
                fileSink.Close();
            }
        }
        catch (Exception)
        {
            // Closing is best effort as well.
        }
    }

    /// <summary>
    /// Routes one record. A throwing sink loses that record, is retired with a single
    /// fallback notice, and every later record goes to stderr; a throwing stderr loses
    /// records as well and is counted, but never reaches a caller.
    /// </summary>
    private static void WriteRecord(ILogSink? sink, string line)
    {
        if (TryFileWrite(sink, line))
        {
            return;
        }

        try
        {
            if (ConsumeFailure(FailureStderr))
            {
                throw new IOException("Injected diagnostic stderr failure.");
            }

            ErrorWriter.WriteLine(line);
        }
        catch (Exception)
        {
            // No diagnostics channel left; the file operation still succeeds.
            Interlocked.Increment(ref _droppedByWriter);
        }
    }

    /// <summary>False when the record must go to stderr instead of the file sink.</summary>
    private static bool TryFileWrite(ILogSink? sink, string line)
    {
        if (sink is null)
        {
            return false;
        }

        var failed = false;
        try
        {
            if (ConsumeFailure(FailureWrite))
            {
                throw new IOException("Injected diagnostic write failure.");
            }

            sink.Write(line);
            // A sink that catches disk-full, ACL or rotation and only sets IsFailed has
            // still lost this record. Treating that as success skips the drop count and
            // the single fallback notice.
            failed = sink.IsFailed;
        }
        catch (Exception)
        {
            failed = true;
        }

        if (!failed)
        {
            return true;
        }

        Interlocked.Increment(ref _droppedByWriter);
        AbandonSink();
        return false;
    }

    private static void Submit(string level, string message, string? correlationId, bool hooked)
    {
        try
        {
            string line;
            try
            {
                if (hooked && ConsumeFailure(FailureFormat))
                {
                    throw new InvalidOperationException("Injected diagnostic formatting failure.");
                }

                line = Logging.Format(new LogEntry(DateTimeOffset.UtcNow, level, message, correlationId));
            }
            catch (Exception ex)
            {
                // Formatting failed: fall back to the literal text, still without throwing.
                line = Logging.Format(new LogEntry(DateTimeOffset.UtcNow, level, Logging.DescribeException(ex), correlationId));
            }

            lock (_gate)
            {
                if (_shutdownRequested || _queue is null)
                {
                    // After the shutdown flag there is no writer left to route a record;
                    // dropping it here is the documented bounded-exit trade.
                    return;
                }

                // The record always goes through the single writer, which routes it to the
                // file sink or, when that sink is unavailable or retired, to stderr. The
                // request path therefore never performs stderr I/O and never blocks on it.
                if (!_queue.TryEnqueue(line))
                {
                    // Bounded queue overflow: the record is dropped and counted instead of
                    // becoming transport back-pressure.
                    return;
                }

                Monitor.PulseAll(_gate);
            }
        }
        catch (Exception)
        {
            // Absolute last resort: diagnostics are never allowed to escape.
        }
    }

    /// <summary>True while this session's file sink is still the destination for records.</summary>
    private static bool IsFileSinkActive()
    {
        lock (_gate)
        {
            return _fileSinkEnabled && _sink is LogFileSink { IsFailed: false };
        }
    }

    /// <summary>
    /// A sink that can no longer accept records is retired once. The notice is produced on
    /// this thread but delivered through the queue, so even the failure report cannot block
    /// the caller; later records go to stderr.
    /// </summary>
    private static void AbandonSink()
    {
        string? notice = null;
        try
        {
            lock (_gate)
            {
                if (_fileSinkEnabled)
                {
                    _fileSinkEnabled = false;
                    if (_queue is not null && !_shutdownRequested)
                    {
                        notice = Logging.Format(new LogEntry(
                            DateTimeOffset.UtcNow,
                            Logging.ErrorLevel,
                            $"File log disabled for '{_activeDirectory ?? Logging.DefaultDirectory()}' (write-failure); diagnostics continue on stderr only.",
                            null));
                        if (!_queue.TryEnqueue(notice))
                        {
                            notice = null;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Retiring a sink and reporting the fallback stay best effort: this method runs
            // on the writer thread, which must never die.
        }
    }

    /// <summary>One safe message on stderr for a file sink that was never usable.</summary>
    private static void ReportSinkDisabled(string directory, string reason)
    {
        try
        {
            ErrorWriter.WriteLine(Logging.Format(new LogEntry(
                DateTimeOffset.UtcNow,
                Logging.ErrorLevel,
                $"File log disabled for '{directory}' ({reason}); diagnostics continue on stderr only.",
                null)));
        }
        catch (Exception)
        {
            // Reporting the fallback is itself best effort.
        }
    }

    /// <summary>
    /// One-shot, stage-specific failure injection: only the configured stage fails, and the
    /// injection is consumed even when the hook itself throws (a failed injection is still
    /// an injected failure).
    /// </summary>
    private static bool ConsumeFailure(int stage)
    {
        var injection = FailNextWrite;
        if (injection is null || injection.Stage != stage)
        {
            return false;
        }

        FailNextWrite = null;
        try
        {
            injection.Hook?.Invoke(stage);
        }
        catch (Exception)
        {
            // A hook that throws is itself the injected failure.
        }

        return true;
    }

    /// <summary>Selects one stage of the next diagnostic record to fail.</summary>
    internal sealed class LogFailureInjection
    {
        internal int Stage { get; }

        internal Action<int>? Hook { get; }

        internal LogFailureInjection(int stage, Action<int>? hook = null)
        {
            Stage = stage;
            Hook = hook;
        }
    }

    private static readonly object _gate = new();
    private static BoundedLogQueue? _queue;
    private static ILogSink? _sink;
    private static Thread? _writer;
    private static string? _activeDirectory;
    private static string? _activeFilePath;
    private static bool _fileSinkEnabled;
    private static bool _shutdownRequested;
    private static TextWriter _errorWriter = Console.Error;
    private static long _droppedByWriter;
}
