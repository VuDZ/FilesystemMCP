namespace FilesystemMcp;

/// <summary>
/// The single deterministic seam the protected test host uses to observe and hold an
/// in-flight tool invocation. It is deliberately inert in production: the production
/// entry point never assigns these callbacks and never reads the test environment
/// variables that configure them, so a shipped server has no injectable delay, fault
/// or delay-based synchronization point.
/// </summary>
/// <remarks>
/// This exists instead of a timer-based test because FS-02/FS-07 forbid sleeps as a
/// synchronization device: a barrier here proves exactly which invocation holds a
/// concurrency slot, which is what the acceptance evidence has to show.
/// </remarks>
internal static class TestHooks
{
    /// <summary>Invoked with the tool name once an invocation holds its concurrency slot.</summary>
    internal static Action<string>? ToolExecutionBarrier
    {
        get; set;
    }

    /// <summary>
    /// Reaches the barrier for <paramref name="toolName"/> when one is installed.
    /// Re-entrancy is impossible (a tool never executes a tool), so the guard only
    /// protects against a defect in the hook itself turning into recursion.
    /// </summary>
    internal static void ReachToolExecution(string toolName)
    {
        var barrier = ToolExecutionBarrier;
        if (barrier is null || _reentered)
        {
            return;
        }

        _reentered = true;
        try
        {
            barrier(toolName);
        }
        finally
        {
            _reentered = false;
        }
    }

    [ThreadStatic]
    private static bool _reentered;
}
