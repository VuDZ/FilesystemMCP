using Microsoft.Win32.SafeHandles;

namespace FilesystemMcp;

/// <summary>
/// Append-only file sink with bounded rotation. Only this session's own names
/// (<c>base</c>, <c>base.1</c> … <c>base.N-1</c>) are read, renamed or removed: startup and
/// rotation never touch a foreign process's logs. Every operation is best effort and a
/// failure only marks the sink unusable.
/// </summary>
internal sealed class LogFileSink : ILogSink
{
    internal const long DefaultMaxBytes = 10 * 1024 * 1024;
    internal const int DefaultMaxFilesPerSession = 5;

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly int _maxFilesPerSession;
    private FileStream? _stream;
    private long _bytesWritten;
    private bool _failed;

    internal LogFileSink(
        string directory,
        string baseFileName,
        long maxBytes = DefaultMaxBytes,
        int maxFilesPerSession = DefaultMaxFilesPerSession)
    {
        CurrentFilePath = Path.Combine(directory, baseFileName);
        BaseFilePath = CurrentFilePath;
        _maxBytes = Math.Max(1, maxBytes);
        _maxFilesPerSession = Math.Max(1, maxFilesPerSession);
        // Only the constructor may throw: the caller decides whether the file sink is
        // available at all. Everything after that is best effort.
        _stream = Open();
        _bytesWritten = _stream.Length;
        // Prove the session file is writable now, so a directory that exists but rejects
        // this file disables the sink at startup instead of losing records later.
        try
        {
            _stream.Flush();
        }
        catch (Exception)
        {
            Fail();
        }
    }

    internal string CurrentFilePath { get; }

    internal string BaseFilePath { get; }

    internal bool IsFailed
    {
        get { lock (_gate) { return _failed; } }
    }

    /// <summary>
    /// One-shot test seam. The next <see cref="Write"/> or <see cref="Flush"/> invokes this
    /// action and clears it, inside the sink's own try, so a swallowed disk-full or ACL
    /// failure is what marks the sink failed. Production never sets it.
    /// </summary>
    internal Action? FailNextOperation { get; set; }

    bool ILogSink.IsFailed => IsFailed;

    public void Write(string line)
    {
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }

            try
            {
                ConsumeFailNextOperation();
                var payload = line + Environment.NewLine;
                if (_bytesWritten + System.Text.Encoding.UTF8.GetByteCount(payload) > _maxBytes)
                {
                    Rotate();
                    if (_failed)
                    {
                        // Rotation already retired the sink. Continuing would open a new
                        // handle and look like a successful write of a record that was lost.
                        return;
                    }
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                if (bytes.Length > _maxBytes)
                {
                    // A single record must not push the file past its bound: the caller
                    // already bounds record length, and this keeps the file bound honest.
                    payload = Truncate(payload, _maxBytes);
                    bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                }

                var stream = _stream ??= Open();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                _bytesWritten += bytes.Length;
            }
            catch (Exception)
            {
                // Losing diagnostics is acceptable; failing a file operation is not.
                Fail();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }

            try
            {
                ConsumeFailNextOperation();
                _stream?.Flush();
            }
            catch (Exception)
            {
                Fail();
            }
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            try
            {
                _stream?.Dispose();
            }
            catch (Exception)
            {
                // Closing a log file cannot be allowed to surface anywhere.
            }
            finally
            {
                _stream = null;
                // Closed means closed: a later write must not silently reopen the file.
                _failed = true;
            }
        }
    }

    /// <summary>Current file, then highest own index down: <c>.3</c>→<c>.4</c>, …, oldest removed.</summary>
    private void Rotate()
    {
        try
        {
            _stream?.Dispose();
            _stream = null;

            var highest = _maxFilesPerSession - 1;
            if (highest >= 1)
            {
                var oldest = Indexed(highest);
                if (File.Exists(oldest))
                {
                    File.Delete(oldest);
                }

                for (var index = highest - 1; index >= 1; index--)
                {
                    var source = Indexed(index);
                    if (File.Exists(source))
                    {
                        File.Move(source, Indexed(index + 1), overwrite: true);
                    }
                }

                File.Move(BaseFilePath, Indexed(1), overwrite: true);
            }
            else
            {
                File.Delete(BaseFilePath);
            }

            _bytesWritten = 0;
            _stream = Open();
        }
        catch (Exception)
        {
            Fail();
        }
    }

    private void ConsumeFailNextOperation()
    {
        var hook = FailNextOperation;
        if (hook is null)
        {
            return;
        }

        FailNextOperation = null;
        hook();
    }

    private string Indexed(int index) => BaseFilePath + "." + index;

    /// <summary>
    /// Append handle that explicitly shares read/write/delete: an operator, a test or a
    /// log shipper must be able to read the file while this process is writing it. The
    /// high-level <see cref="FileMode.Append"/> shortcut opens without read sharing, which
    /// is exactly the "log file is locked" complaint this contract exists to prevent.
    /// </summary>
    private FileStream Open()
    {
        SafeFileHandle? handle = null;
        try
        {
            // Build the append handle by hand so sharing is explicit and an existing file
            // is never truncated.
            handle = File.OpenHandle(
                CurrentFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var stream = new FileStream(handle, FileAccess.Write, bufferSize: 4096);
            handle = null;
            return stream;
        }
        finally
        {
            // A failure after the handle exists must not leak the descriptor.
            handle?.Dispose();
        }
    }

    /// <summary>Byte-bounded truncation that never splits a UTF-8 sequence.</summary>
    private static string Truncate(string value, long maxBytes)
    {
        const string marker = "…[truncated]";
        var budget = maxBytes - System.Text.Encoding.UTF8.GetByteCount(marker);
        if (budget <= 0)
        {
            return marker;
        }

        var length = value.Length;
        while (length > 0 && System.Text.Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > budget)
        {
            length--;
        }

        return string.Concat(value.AsSpan(0, length), marker);
    }

    private void Fail()
    {
        _failed = true;
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Already failing: nothing further to report.
        }

        _stream = null;
    }
}
