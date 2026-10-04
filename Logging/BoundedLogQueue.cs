namespace FilesystemMcp;

/// <summary>
/// Single bounded buffer between callers and the one logger writer thread. Enqueue never
/// blocks the request path: when the buffer is full the record is dropped and counted,
/// which is the documented trade for "logging must not become a failure or a stall".
/// </summary>
internal sealed class BoundedLogQueue
{
    internal int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    internal long PendingBytes
    {
        get
        {
            lock (_gate)
            {
                return _queuedBytes;
            }
        }
    }

    internal BoundedLogQueue(int maxEntries = 4096, long maxBytes = 1024 * 1024, int maxEntryBytes = 8192)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes = Math.Max(1, maxBytes);
        _maxEntryBytes = Math.Max(64, maxEntryBytes);
    }

    internal bool TryEnqueue(string line)
    {
        if (line.Length > _maxEntryBytes)
        {
            line = Truncate(line, _maxEntryBytes);
        }

        lock (_gate)
        {
            if (_lines.Count >= _maxEntries || _queuedBytes + line.Length > _maxBytes)
            {
                _dropped++;
                return false;
            }

            _lines.Enqueue(line);
            _queuedBytes += line.Length;
            return true;
        }
    }

    internal bool TryDequeue(out string line)
    {
        lock (_gate)
        {
            if (_lines.Count == 0)
            {
                line = string.Empty;
                return false;
            }

            line = _lines.Dequeue();
            _queuedBytes -= line.Length;
            return true;
        }
    }

    /// <summary>Moves as much as the deadline allows; used only for the bounded exit flush.</summary>
    internal int DrainRemaining(List<string> buffer, DateTimeOffset deadline)
    {
        var drained = 0;
        while (DateTimeOffset.UtcNow <= deadline && TryDequeue(out var line))
        {
            buffer.Add(line);
            drained++;
        }

        return drained;
    }

    private static string Truncate(string value, int maxLength)
    {
        var marker = "…[truncated, " + value.Length + " chars]";
        var keep = maxLength - marker.Length;
        if (keep < 0)
        {
            keep = 0;
        }

        if (keep > value.Length)
        {
            keep = value.Length;
        }

        return string.Concat(value.AsSpan(0, keep), marker);
    }

    private readonly Queue<string> _lines = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly int _maxEntryBytes;
    private readonly object _gate = new();
    private long _queuedBytes;
    private int _dropped;
}
