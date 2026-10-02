using FilesystemMcp;

namespace FilesystemMcp.Tests;

// TEMPORARY probe: is the ambient RequestDeadline visible from an async continuation and
// from a Task.Run launched by that continuation? Deleted before delivery.
[Trait("Spec", "FS-07-PROBE")]
public sealed class AmbientProbeTests
{
    [Fact]
    public async Task AmbientIsVisibleFromContinuationAndTaskRun()
    {
        using var cts = new CancellationTokenSource();
        using var scope = RequestDeadline.Begin(cts.Token);
        var atStart = RequestDeadline.Token is not null;

        await Task.Yield();
        var afterYield = RequestDeadline.Token is not null;

        var insideTaskRun = await Task.Run(() => RequestDeadline.Token is not null);

        Task<bool> Deferred() => Task.Run(() =>
        {
            // Mirrors ToolRegistry.InvokeAsync: a Task.Run created by the transport's
            // dispatch task, i.e. after the scope was opened.
            using var inner = RequestDeadline.Begin(RequestDeadline.Token ?? CancellationToken.None);
            return RequestDeadline.Token is not null;
        });

        var deferred = await Deferred();
        Assert.Fail($"start={atStart} afterYield={afterYield} insideTaskRun={insideTaskRun} deferred={deferred}");
    }
}
