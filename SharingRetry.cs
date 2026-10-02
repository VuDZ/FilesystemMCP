namespace FilesystemMcp;

/// <summary>
/// Bounded retry for transient sharing violations only (FS-04). Access-denied,
/// missing-file and every mutation outcome are never retried; hash conflicts and
/// uncertain commits stay untouched. Waits honour the caller's token and use a
/// monotonic <see cref="TimeProvider"/> for the overall deadline. The concrete
/// budgets/deadlines of FS-07 are intentionally not implemented here.
/// </summary>
internal static class SharingRetry
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100)
    ];

    internal static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action<int>? beforeBackoff,
        CancellationToken cancellationToken) =>
        RunAsync(operation, beforeBackoff, TimeProvider.System, delayAsync: null, cancellationToken);

    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action<int>? beforeBackoff,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        CancellationToken cancellationToken,
        Func<Exception, bool>? isRetryable = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(timeProvider);
        // The retry predicate is injectable so deadline-bounding can be exercised on
        // any host; production always uses the sharing-violation classifier.
        var retryable = isRetryable ?? FileErrorClassifier.IsSharingViolation;
        var start = timeProvider.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && retryable(ex))
            {
                // The remaining deadline bounds the wait itself, not just the next
                // attempt: never sleep past the deadline, and stop retrying once it
                // has elapsed.
                var remaining = Deadline - timeProvider.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero) throw;
                beforeBackoff?.Invoke(attempt + 1);
                var backoff = Backoff[attempt - 1];
                var wait = remaining < backoff ? remaining : backoff;
                if (delayAsync is null)
                {
                    await Task.Delay(wait, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await delayAsync(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
