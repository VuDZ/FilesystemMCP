namespace FilesystemMcp.Tests;

/// <summary>
/// Guards the mechanism the FS-07 deadline discriminator rests on. <see cref="RequestDeadline"/>
/// publishes the timer-only token through an <see cref="AsyncLocal{T}"/>, and a tool runs in
/// a <c>Task.Run</c> created by the transport's dispatch task — so the value has to survive
/// both an async continuation and that nested task. When it does not, the discriminator
/// silently reports every fired deadline as a peer cancellation, which is exactly the defect
/// that shipped once already.
/// </summary>
[Trait("Spec", "FS-07")]
public sealed class RequestDeadlineTests
{
    [Fact, Trait("Status", "Baseline")]
    public async Task AmbientDeadlineSurvivesContinuationsAndTheToolTaskBoundary()
    {
        using var deadline = new CancellationTokenSource();

        using (RequestDeadline.Begin(deadline.Token))
        {
            Assert.NotNull(RequestDeadline.Token);
            Assert.False(RequestDeadline.IsDeadline(), "A deadline that has not fired is not the cause.");

            await Task.Yield();
            Assert.NotNull(RequestDeadline.Token);

            // Mirrors ToolRegistry.InvokeAsync: the tool body is a nested Task.Run that
            // re-publishes the ambient token rather than deriving one from its own.
            var insideToolTask = await Task.Run(() =>
            {
                using var inner = RequestDeadline.Begin(RequestDeadline.Token ?? CancellationToken.None);
                deadline.Cancel();
                return RequestDeadline.IsDeadline();
            });

            Assert.True(insideToolTask, "A fired deadline must be visible from inside the tool task.");
            Assert.True(RequestDeadline.IsDeadline());
        }

        // The scope restores what it replaced, so a tool never observes a deadline that
        // belongs to another request.
        Assert.Null(RequestDeadline.Token);
        Assert.False(RequestDeadline.IsDeadline());
    }
}
