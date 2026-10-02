namespace FilesystemMcp;

/// <summary>
/// Bounds how many read-only tool invocations run at the same time
/// (<c>maxConcurrentReads</c>). Reads, searches and directory listings share this gate;
/// mutations are deliberately outside it, because FS-02 already serializes them per
/// file identity and a queued writer must not consume read capacity.
/// </summary>
/// <remarks>
/// A waiter counts against the budget only while it is actually running: the gate is
/// acquired inside the tool task, so a request that is cancelled while queued leaves
/// without ever holding a slot. The wait itself observes the request token, so a
/// cancelled or timed-out request cannot be parked here indefinitely.
/// </remarks>
internal sealed class ResourceLimiter
{
    private readonly SemaphoreSlim _gate;

    public ResourceLimiter(int maxConcurrentReads)
    {
        if (maxConcurrentReads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentReads));
        }

        _gate = new SemaphoreSlim(maxConcurrentReads, maxConcurrentReads);
    }

    public int MaxConcurrentReads => _gate.CurrentCount;

    /// <summary>Number of invocations currently holding a slot; test-observable.</summary>
    public int ActiveCount { get; private set; }

    /// <summary>Highest number of simultaneous holders observed; test-observable.</summary>
    public int PeakActiveCount { get; private set; }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (this)
            {
                ActiveCount++;
                if (ActiveCount > PeakActiveCount)
                {
                    PeakActiveCount = ActiveCount;
                }
            }

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (this)
            {
                ActiveCount--;
            }

            _gate.Release();
        }
    }
}
