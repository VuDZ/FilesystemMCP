namespace FilesystemMcp;

// Only internal callers (including the protected test host) can supply faults.
internal sealed class AtomicWriteDependencies
{
    internal Action<AtomicWritePoint>? Hook { get; init; }
    internal Action<Stream, byte[], int, int> WriteChunk { get; init; } = (stream, bytes, offset, count) => stream.Write(bytes, offset, count);
    internal Action<FileStream> Flush { get; init; } = stream => stream.Flush(flushToDisk: true);
    internal void Invoke(AtomicWritePoint point) => Hook?.Invoke(point);
}
