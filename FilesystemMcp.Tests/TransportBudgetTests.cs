using System.Text;
using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

/// <summary>
/// FS-07 at the stdio level: the byte budget of one frame, the character budget of one
/// response, peer cancellation, the operation deadline, the read-parallelism budget and
/// the responsiveness of the reader loop while a tool holds its slot.
/// </summary>
/// <remarks>
/// Every ordering in this file is produced by a protocol frame or by the protected-host
/// named-pipe barrier (<see cref="ToolExecutionGate"/>), never by a delay: a parked
/// invocation is proof that a request reached the tool body while holding its slot, and
/// that is exactly what the concurrency contract is about. The only waits are watchdogs
/// that fail a hung test, plus one bounded wait that lets the server-side operation
/// deadline fire while its invocation is parked (a timer cannot be observed any other way
/// from outside the process).
/// </remarks>
[Trait("Spec", "FS-07")]
public sealed class TransportBudgetTests
{
    /// <summary>Frame budget used by the oversized/boundary cases.</summary>
    private const int RequestBudget = 1024;

    /// <summary>Payload budget used by the response case.</summary>
    private const int ResponseBudget = 600;

    /// <summary>Deadline of the parked-write case; the test lets it fire before releasing.</summary>
    private const long DeadlineMilliseconds = 300;

    /// <summary>
    /// Extra wall-clock time after <see cref="DeadlineMilliseconds"/>.
    /// <c>CancelAfter</c> is a timer on the server process. A parallel suite has
    /// delayed that timer by more than a small multiple of a 300 ms budget; releasing
    /// then lets the write commit, and the case fails even though a fired deadline
    /// would have kept the original bytes. Run alone, the timer is on time.
    /// </summary>
    private static readonly TimeSpan DeadlineTimerSlack = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Failure deadline for responses that are only produced after the test releases a
    /// barrier the test itself holds. It is a watchdog, not a budget for the server.
    /// </summary>
    private static readonly TimeSpan ResponseWatchdog = TimeSpan.FromSeconds(20);

    // ---- 1. oversized frame ----

    [Fact, Trait("Status", "Baseline")]
    public async Task OversizedFrameIsRefusedWithoutExecutingAndTheStreamStaysUsable()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, [$"--maxRequestBytes={RequestBudget}"]);
        var target = Path.Combine(sandbox.Workspace, "must-not-exist.txt");

        // 3000 characters of content: the frame is far beyond the 1024 byte budget.
        var frame = CreateFileFrame(900, "must-not-exist.txt", new string('x', 3000));
        Assert.True(Encoding.UTF8.GetByteCount(frame) > RequestBudget, "the fixture frame must exceed the budget");
        await server.SendRawLineAsync(frame);

        var refused = await server.ReadAsync(ResponseWatchdog);
        McpAssert.ProtocolError(refused, -32600);
        AssertUnattributed(refused);
        // Not even partially: nothing was created from the frame that was refused.
        Assert.False(File.Exists(target), "the oversized frame was executed");

        // The reader drained the line and kept the stream aligned.
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
        var created = await server.ToolAsync("create_file", new { path = "after.txt", content = "after" });
        McpAssert.Success(created);
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "after.txt")));
    }

    // ---- 2. frame at the budget ----

    [Fact, Trait("Status", "Baseline")]
    public async Task FrameAtAndBelowTheRequestBudgetIsAcceptedAndOneByteOverIsRefused()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, [$"--maxRequestBytes={RequestBudget}"]);

        // Three frames: the largest CRLF-terminated frame that fits, a payload exactly at
        // the budget, and one byte over. The budget is a maximum, so the boundary is
        // inclusive; every size is produced and verified by the fixture, not assumed.
        var belowFrame = CreateFileFrameOfSize(910, "below.txt", RequestBudget - 1, out var belowContent);
        await server.SendRawLineAsync(belowFrame);
        McpAssert.Success(await server.ReadResponseAsync(910, ResponseWatchdog));
        Assert.Equal(belowContent, await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "below.txt")));

        // Terminated with a bare LF: the budget is the frame budget, and the CR of a CRLF
        // terminator is not part of the frame (the reader strips it only after the budget
        // check, so a CRLF frame whose payload is exactly maxRequestBytes counts as one
        // byte over).
        var exactFrame = CreateFileFrameOfSize(911, "exact.txt", RequestBudget, out var exactContent);
        await server.SendRawBytesAsync(Encoding.UTF8.GetBytes(exactFrame + "\n"));
        McpAssert.Success(await server.ReadResponseAsync(911, ResponseWatchdog));
        Assert.Equal(exactContent, await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "exact.txt")));

        var overFrame = CreateFileFrameOfSize(912, "over.txt", RequestBudget + 1, out _);
        await server.SendRawLineAsync(overFrame);
        McpAssert.ProtocolError(await server.ReadAsync(ResponseWatchdog), -32600);
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "over.txt")), "a frame one byte over the budget was executed");
    }

    // ---- 3. invalid UTF-8 ----

    [Fact, Trait("Status", "Baseline")]
    public async Task InvalidUtf8FrameIsAParseErrorAndTheSessionSurvives()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);

        // A syntactically valid frame whose string payload carries 0xFF, a byte that can
        // never appear inside UTF-8 text. Written at byte level: a text writer can only
        // emit valid UTF-8, so it could not produce this frame at all.
        var frame = Encoding.ASCII.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":903,\"method\":\"ping\",\"params\":{\"x\":\"?\"}}\n");
        var placeholder = Array.IndexOf(frame, (byte)'?');
        Assert.InRange(placeholder, 0, frame.Length - 1);
        frame[placeholder] = 0xFF;
        await server.SendRawBytesAsync(frame);

        var reply = await server.ReadAsync(ResponseWatchdog);
        McpAssert.ProtocolError(reply, -32700);
        AssertUnattributed(reply);
        // The frame is not silently repaired into a valid ping either.
        Assert.False(reply.TryGetProperty("result", out _), "invalid UTF-8 must not be replaced and executed: " + reply);

        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    // ---- 4. response budget ----

    [Fact, Trait("Status", "Baseline")]
    public async Task PayloadOverTheResponseBudgetBecomesABoundedResourceLimitResult()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("big.txt", new string('a', 5000));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, [$"--maxResponseChars={ResponseBudget}"]);

        // The request itself fits the (default) request budget; only the answer cannot.
        var id = await server.SendToolAsync("read_file", new { path = "big.txt" });
        var line = await server.ReadRawLineAsync(ResponseWatchdog);

        // The delivered line is complete, valid JSON, attributed to the request and
        // strictly inside the response budget — never a truncated payload.
        var reply = ServerProcess.JsonDocumentParse(line);
        Assert.Equal(id, reply.GetProperty("id").GetInt32());
        Assert.True(line.Length < ResponseBudget, $"the delivered frame is {line.Length} characters, over maxResponseChars={ResponseBudget}");
        Assert.True(Encoding.UTF8.GetByteCount(line) < ResponseBudget, "the delivered frame is over the byte budget as well");
        McpAssert.ToolError(reply, "resource_limit");
    }

    // ---- 5. ping while a tool holds its slot ----

    [Fact, Trait("Status", "Baseline")]
    public async Task PingIsAnsweredWhileAToolIsParkedOnTheBarrier()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("held.txt", "held content");
        await using var gate = new ToolExecutionGate("read_file");
        await using var server = await gate.Start(sandbox.Workspace);

        var toolId = await server.SendToolAsync("read_file", new { path = "held.txt" });
        Assert.Equal("read_file", await gate.At("read_file"));
        var handshakeFrames = server.ReceivedFrameCount;

        // The invocation holds its slot and is parked inside the host, so it cannot have
        // written anything yet. A ping sent now must be answered by the reader loop.
        var pingId = await server.SendCallAsync("ping", new { });
        var ping = await server.ReadResponseAsync(pingId, ResponseWatchdog);
        Assert.True(ping.TryGetProperty("result", out _), "the ping must be answered while the tool is parked: " + ping);

        // Observed order: the ping's frame is the only frame produced since the barrier.
        var whileParked = server.FramesSince(handshakeFrames);
        Assert.Single(whileParked);
        Assert.Equal(pingId, ServerProcess.JsonDocumentParse(whileParked[0]).GetProperty("id").GetInt32());
        // The frame is complete and exactly one line: nothing was interleaved into it while
        // the tool held its slot (a ping result carries a fixed, empty payload).
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":" + pingId + ",\"result\":{}}", whileParked[0]);

        await gate.Release();
        var read = await server.ReadResponseAsync(toolId, ResponseWatchdog);
        Assert.Equal("held content", ServerProcess.Payload(read).GetProperty("text").GetString());

        var afterRelease = server.FramesSince(handshakeFrames);
        Assert.Equal(2, afterRelease.Count);
        Assert.Equal(pingId, ServerProcess.JsonDocumentParse(afterRelease[0]).GetProperty("id").GetInt32());
        Assert.Equal(toolId, ServerProcess.JsonDocumentParse(afterRelease[1]).GetProperty("id").GetInt32());
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    // ---- 6a. peer cancellation of a parked tool ----

    [Fact, Trait("Status", "Baseline")]
    public async Task PeerCancellationOfAParkedToolIsReportedAsCancelled()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("held.txt", "held content");
        await using var gate = new ToolExecutionGate("read_file");
        await using var server = await gate.Start(sandbox.Workspace);

        var id = await server.SendToolAsync("read_file", new { path = "held.txt" });
        Assert.Equal("read_file", await gate.At("read_file"));

        // notifications/cancelled is a notification: it is dispatched on the reader loop
        // while the tool is parked. The ping that follows it on the same loop proves the
        // cancel was processed before the barrier was released.
        await server.NotifyAsync("notifications/cancelled", new { requestId = id });
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));

        // A parked invocation cannot answer by itself, so releasing it is part of the
        // setup: what it must answer after the release is the cancellation, not the read.
        await gate.Release();
        var response = await server.ReadResponseAsync(id, ResponseWatchdog);
        McpAssert.ToolError(response, "cancelled");

        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
        Assert.Equal("held content", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "held.txt")));
    }

    // ---- 6b. operation deadline of a parked write ----

    [Fact, Trait("Status", "Baseline")]
    public async Task OperationDeadlineOfAParkedWriteKeepsOriginalBytesAndReportsResourceLimit()
    {
        using var sandbox = new Sandbox();
        const string original = "original content\nsecond line\n";
        var path = sandbox.Write("file.txt", original);
        var hash = FileTextHelper.ComputeContentHashes(original).Sha256;
        await using var gate = new ToolExecutionGate("replace_in_file");
        await using var server = await gate.Start(sandbox.Workspace, [$"--operationTimeoutMs={DeadlineMilliseconds}"]);

        var id = await server.SendToolAsync("replace_in_file", new
        {
            path = "file.txt",
            target_snippet = "original",
            replacement_snippet = "changed",
            original_hash = hash
        });
        Assert.Equal("replace_in_file", await gate.At("replace_in_file"));

        // The deadline is a timer inside the server and the parked invocation cannot
        // observe it until it returns from the barrier, so this bounded wait is the only
        // way to let it fire. The slack covers a timer queue that runs late under
        // parallel load. Nothing is asserted about how long anything took: the
        // assertions below are about the response and the bytes after the release.
        await LetDeadlineElapseAsync(DeadlineMilliseconds);

        await gate.Release();
        var response = await server.ReadResponseAsync(id, ResponseWatchdog);
        McpAssert.ToolError(response, "resource_limit");

        // FS-02: a deadline before the commit never touches the original.
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(hash, FileTextHelper.ComputeContentHashes(await File.ReadAllTextAsync(path)).Sha256);
        Assert.Empty(Directory.GetFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    // ---- 7a. one read slot ----

    [Fact, Trait("Status", "Baseline")]
    public async Task OneReadSlotSerializesToolBodiesAndBothCallsStillSucceed()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("a.txt", "alpha");
        sandbox.Write("b.txt", "beta");
        await using var gate = new ToolExecutionGate("read_file");
        await using var server = await gate.Start(sandbox.Workspace, ["--maxConcurrentReads=1"]);

        var first = await server.SendToolAsync("read_file", new { path = "a.txt" });
        Assert.Equal("read_file", await gate.At("read_file"));

        // The second read cannot reach its tool body: it waits for the single slot. That
        // is asserted without a clock. A queued request still observes its own token, so
        // cancelling it produces a response while the first invocation is parked; a
        // request that had entered the body would be sitting in the barrier and could not
        // answer before its release. The barrier therefore reported exactly one holder
        // before the first release.
        var queued = await server.SendToolAsync("read_file", new { path = "b.txt" });
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
        await server.NotifyAsync("notifications/cancelled", new { requestId = queued });
        McpAssert.ToolError(await server.ReadResponseAsync(queued, ResponseWatchdog), "cancelled");

        await gate.Release();
        Assert.Equal("alpha", ServerProcess.Payload(await server.ReadResponseAsync(first, ResponseWatchdog)).GetProperty("text").GetString());

        // Two calls, one slot, two releases: the second call reaches the body only after
        // the first invocation left it, and both answers are successful reads. Which of the
        // two queued calls wins the free slot is not a contract, so each answer is matched
        // to its request by id instead of by arrival order.
        var third = await server.SendToolAsync("read_file", new { path = "a.txt" });
        var fourth = await server.SendToolAsync("read_file", new { path = "b.txt" });
        Assert.Equal("read_file", await gate.At("read_file"));
        await gate.Release();
        // The next report can only come from the other call: the one slot is still held
        // until the released invocation has left its body.
        Assert.Equal("read_file", await gate.At("read_file"));
        var firstAnswer = await server.ReadAsync(ResponseWatchdog);
        await gate.Release();
        var secondAnswer = await server.ReadAsync(ResponseWatchdog);
        var texts = new[] { firstAnswer, secondAnswer }.ToDictionary(
            response => response.GetProperty("id").GetInt32(),
            response => ServerProcess.Payload(response).GetProperty("text").GetString());
        Assert.Equal("alpha", texts[third]);
        Assert.Equal("beta", texts[fourth]);
    }

    // ---- 7b. two read slots ----

    [Fact, Trait("Status", "Baseline")]
    public async Task TwoReadSlotsLetTwoInvocationsReachTheBarrierBeforeAnyRelease()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("a.txt", "alpha");
        sandbox.Write("b.txt", "beta");
        await using var gate = new ToolExecutionGate("read_file");
        await using var server = await gate.Start(sandbox.Workspace, ["--maxConcurrentReads=2"]);

        var one = await server.SendToolAsync("read_file", new { path = "a.txt" });
        var two = await server.SendToolAsync("read_file", new { path = "b.txt" });

        // Both invocations are inside their tool bodies at the same time: each reports the
        // barrier on its own, before any release. A single slot could not produce the
        // second report, and the gate watchdog would fail the test instead.
        Assert.Equal("read_file", await gate.At("read_file"));
        Assert.Equal("read_file", await gate.At("read_file"));

        await gate.Release();
        await gate.Release();
        // Which parked invocation consumes which release is not a contract, so the answers
        // are matched by request id instead of by arrival order.
        var responses = new[]
        {
            await server.ReadAsync(ResponseWatchdog),
            await server.ReadAsync(ResponseWatchdog)
        };
        var texts = responses.ToDictionary(
            response => response.GetProperty("id").GetInt32(),
            response => ServerProcess.Payload(response).GetProperty("text").GetString());
        Assert.Equal("alpha", texts[one]);
        Assert.Equal("beta", texts[two]);
    }

    // ---- 8. cancellation that matches nothing ----

    [Fact, Trait("Status", "Baseline")]
    public async Task UnknownAndFinishedCancellationRequestsDoNotDisturbTheSession()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "content");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var handshakeFrames = server.ReceivedFrameCount;

        var finished = await server.SendToolAsync("read_file", new { path = "file.txt" });
        Assert.Equal("content", ServerProcess.Payload(await server.ReadResponseAsync(finished, ResponseWatchdog)).GetProperty("text").GetString());

        // An id nobody is waiting for, then the id of a request that already answered.
        await server.NotifyAsync("notifications/cancelled", new { requestId = 987654 });
        await server.NotifyAsync("notifications/cancelled", new { requestId = finished });

        var ping = await server.SendCallAsync("ping", new { });
        Assert.True((await server.ReadResponseAsync(ping, ResponseWatchdog)).TryGetProperty("result", out _));
        var created = await server.SendToolAsync("create_file", new { path = "after.txt", content = "after" });
        McpAssert.Success(await server.ReadResponseAsync(created, ResponseWatchdog));
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "after.txt")));

        // Neither notification produced a frame: the observed frames are exactly the
        // three requests, in order.
        var observed = server.FramesSince(handshakeFrames)
            .Select(frame => ServerProcess.JsonDocumentParse(frame).GetProperty("id").GetInt32())
            .ToArray();
        Assert.Equal(new[] { finished, ping, created }, observed);
    }

    // ---- 9. startup validation ----

    [Fact, Trait("Status", "Baseline")]
    public async Task EveryBudgetOptionRejectsDuplicateZeroNegativeAndNonNumericValues()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("budget-option-server", protectedHost: true);
        var isDll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var host = isDll ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : executable;
        string[] names =
        [
            ResourceBudgetNames.MaxRequestBytes, ResourceBudgetNames.MaxFileBytes, ResourceBudgetNames.MaxLineChars,
            ResourceBudgetNames.MaxResponseChars, ResourceBudgetNames.SearchMaxFiles, ResourceBudgetNames.SearchMaxDirectories,
            ResourceBudgetNames.OperationTimeoutMs, ResourceBudgetNames.MaxConcurrentReads
        ];

        foreach (var name in names)
        {
            string[][] rejected =
            [
                [$"--{name}=2", $"--{name}=2"],
                [$"--{name}=0"],
                [$"--{name}=-1"],
                [$"--{name}=abc"]
            ];
            foreach (var extra in rejected)
            {
                var arguments = new List<string>();
                if (isDll) arguments.Add(executable);
                arguments.Add(sandbox.Workspace);
                arguments.AddRange(extra);
                var result = await ProcessRunner.RunWithClosedInputAsync(host, arguments);
                var shown = string.Join(' ', extra);
                Assert.True(result.ExitCode != 0, $"'{shown}' must fail startup; exit code was {result.ExitCode}");
                Assert.Contains("Startup failed:", result.Stderr);
                // stdout carries protocol frames only, and a startup failure has none.
                Assert.Equal("", result.Stdout);
            }
        }
    }

    // ---- 10. unsupported notification ----

    [Fact, Trait("Status", "Baseline")]
    public async Task CancellationNotificationWithoutParamsProducesNoFrame()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var handshakeFrames = server.ReceivedFrameCount;

        // No params at all: there is no requestId to cancel and nothing to answer.
        await server.SendRawLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\"}");
        var first = await server.SendCallAsync("ping", new { });
        Assert.True((await server.ReadResponseAsync(first, ResponseWatchdog)).TryGetProperty("result", out _));

        // Exactly one frame per ping: a reply to the notification would have been consumed
        // by one of these reads instead of the ping's own answer.
        var second = await server.SendCallAsync("ping", new { });
        Assert.True((await server.ReadResponseAsync(second, ResponseWatchdog)).TryGetProperty("result", out _));
        Assert.Equal(2, server.FramesSince(handshakeFrames).Count);
    }

    // ---- 11. startup validation of the operation deadline --------------------------------

    /// <summary>
    /// <c>CancellationTokenSource.CancelAfter(TimeSpan)</c> is implemented with a 32-bit
    /// millisecond timer, so <see cref="ResourceBudget.MaxOperationTimeoutMs"/> is the
    /// largest schedulable budget. Before that bound was validated, every value below
    /// started a server that armed a deadline it could not schedule and then answered
    /// nothing at all for the whole session: the request task faulted before its reply was
    /// built, so the client saw zero frames while the process stayed alive. A startup
    /// failure is the only honest outcome, and it must not carry protocol frames on stdout.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task OperationTimeoutAboveTheSchedulableTimerMaximumIsAStartupFailure()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("timeout-option-server", protectedHost: true);
        var isDll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var host = isDll ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : executable;

        long[] rejected =
        [
            ResourceBudget.MaxOperationTimeoutMs + 1,
            ResourceBudget.MaxOperationTimeoutMs + 2,
            31_536_000_000,
            9_223_372_036_854_775_806,
            // Measured, not assumed: the ceiling is a hard bound on every larger value, so
            // long.MaxValue is a startup failure too, even though Program's
            // "OperationTimeoutMs < long.MaxValue" guard reads as if it meant "no deadline".
            // That guard is therefore unreachable from the CLI; the observable contract is
            // that anything above MaxOperationTimeoutMs is refused before a session starts.
            long.MaxValue
        ];
        foreach (var value in rejected)
        {
            var arguments = new List<string>();
            if (isDll) arguments.Add(executable);
            arguments.Add(sandbox.Workspace);
            arguments.Add($"--operationTimeoutMs={value}");
            var result = await ProcessRunner.RunWithClosedInputAsync(host, arguments);
            Assert.True(result.ExitCode != 0, $"--operationTimeoutMs={value} must fail startup; exit code was {result.ExitCode}");
            Assert.Contains("Startup failed:", result.Stderr);
            // stdout carries protocol frames only, and a startup failure has none.
            Assert.Equal("", result.Stdout);
        }
    }

    /// <summary>
    /// The boundary and the ordinary values around it must keep working end to end. A
    /// regression here is not a wrong answer but a missing one: the deadline is armed
    /// before the reply is built, so a value the runtime rejects leaves the client with no
    /// frame at all. Each assertion below therefore fails on an absent reply, not only on a
    /// malformed one.
    /// </summary>
    [Theory, Trait("Status", "Baseline")]
    [InlineData(ResourceBudget.MaxOperationTimeoutMs)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(2_147_483_647L)]
    [InlineData(86_400_000L)]
    public async Task AcceptedOperationTimeoutStillAnswersInitializePingAndAToolCall(long value)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "content");
        // StartAsync performs the handshake, which already asserts that the initialize reply
        // arrived; the option is the only difference from every other stdio test here.
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, [$"--operationTimeoutMs={value}"]);

        var ping = await server.SendCallAsync("ping", new { });
        Assert.True((await server.ReadResponseAsync(ping, ResponseWatchdog)).TryGetProperty("result", out _),
            $"--operationTimeoutMs={value} did not answer ping.");

        var call = await server.SendToolAsync("read_file", new { path = "file.txt" });
        var reply = await server.ReadResponseAsync(call, ResponseWatchdog);
        Assert.True(reply.TryGetProperty("result", out var result),
            $"--operationTimeoutMs={value} did not answer tools/call: {reply}");
        Assert.True(result.TryGetProperty("isError", out var isError)
            && isError.ValueKind is JsonValueKind.True or JsonValueKind.False,
            $"tools/call was answered without an isError member: {reply}");
        if (isError.GetBoolean())
        {
            // A 1 or 2 millisecond budget can genuinely expire before the read completes.
            // That is the bounded FS-07 refusal, and the property under test is that such a
            // server still produces a complete attributed reply instead of staying silent.
            McpAssert.ToolError(reply, "resource_limit");
        }
        else
        {
            Assert.Equal("content", ServerProcess.Payload(reply).GetProperty("text").GetString());
        }

        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    // ---- 12. response budget: the refusal must itself fit ---------------------------------

    /// <summary>
    /// The response budget bounds its own replacement. Every delivered line is one complete
    /// JSON document that carries the request id; a frame is never truncated, and once the
    /// budget is at or above the length of the shortest frame the transport can emit, no
    /// delivered frame exceeds the budget either. Below that floor a parseable frame that
    /// carries the id is still better than a cut one.
    /// </summary>
    /// <remarks>
    /// The floor is measured here, not assumed: it is the minimal JSON-RPC error frame for
    /// the request id, and its length depends on that id. The <c>tools/call</c> case is the
    /// tool-error envelope path (its payload fits the tool's own budget, only the finished
    /// frame does not) and <c>tools/list</c> plus the oversized <c>-32601</c> are the
    /// non-<c>content</c> paths, which can only be answered by a JSON-RPC error.
    /// </remarks>
    [Theory, Trait("Status", "Baseline")]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(98)]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(600)]
    public async Task EveryDeliveredFrameIsWholeJsonCarryingTheRequestIdAtAnyResponseBudget(int budget)
    {
        using var sandbox = new Sandbox();
        // 400 quotes escape to 800 characters inside the JSON payload: the text fits the
        // tool's own payload budget, the finished frame does not, so the transport has to
        // replace a result that is otherwise perfectly legal.
        sandbox.Write("quotes.txt", new string('"', 400));
        // No handshake: at these budgets the initialize reply would be replaced as well, and
        // the frames under test are the tool and list answers.
        await using var server = await ServerProcess.StartAsync(
            sandbox.Workspace, [$"--maxResponseChars={budget}"], initialize: false);

        const int readId = 4242;
        const int listId = 4343;
        const int methodId = 4444;
        const int pingId = 4445;
        var floor = MinimalFrame(readId).Length;
        Assert.Equal(floor, MinimalFrame(listId).Length);

        // FS-13 closes tools until initialize and notifications/initialized. The handshake
        // frames are consumed here so the budget assertions below still see the tool, list
        // and unknown-method replies. At a small budget the initialize result is replaced
        // by the same minimal error this test already measures; the session still opens.
        await server.SendRawLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":4141,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"tests\",\"version\":\"1\"}}}");
        var initializeLine = await server.ReadRawLineAsync(ResponseWatchdog);
        AssertWholeFrame(initializeLine, 4141, budget, floor);
        await server.SendRawLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

        await server.SendRawLineAsync(ToolCallFrame(readId, "read_file", new { path = "quotes.txt" }));
        var readLine = await server.ReadRawLineAsync(ResponseWatchdog);
        AssertWholeFrame(readLine, readId, budget, floor);
        var read = ServerProcess.JsonDocumentParse(readLine);
        if (read.TryGetProperty("result", out var readResult))
        {
            // The bounded refusal keeps the tools/call envelope, so the client still reads a
            // tool failure with its machine-readable code. This branch is reachable only
            // when even the refusal fits, i.e. at the larger budgets of this theory.
            Assert.True(readResult.GetProperty("isError").GetBoolean(),
                "An over-budget tool result must be replaced by an error result, not by a success: " + readLine);
            var payload = ServerProcess.JsonDocumentParse(readResult.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal("resource_limit", payload.GetProperty("code").GetString());
        }
        else
        {
            // Below the size of the bounded refusal the transport falls back to the minimal
            // bounded JSON-RPC error: still attributed, still parseable, never a cut payload.
            McpAssert.ProtocolError(read, -32603);
            Assert.Contains("maxResponseChars", read.GetProperty("error").GetProperty("message").GetString());
            Assert.False(read.TryGetProperty("result", out _), "an error frame must not carry a result: " + readLine);
        }

        await server.SendRawLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + listId + ",\"method\":\"tools/list\",\"params\":{}}");
        var listLine = await server.ReadRawLineAsync(ResponseWatchdog);
        AssertWholeFrame(listLine, listId, budget, floor);
        var list = ServerProcess.JsonDocumentParse(listLine);
        // tools/list has no content envelope at all, so there is nothing an isError result
        // could describe: the replacement is a JSON-RPC error that names the budget instead
        // of the old unconditional "Internal error" that pretended the server broke.
        McpAssert.ProtocolError(list, -32603);
        Assert.Contains("maxResponseChars", list.GetProperty("error").GetProperty("message").GetString());
        Assert.False(list.TryGetProperty("result", out _),
            "tools/list must not be answered with a fabricated result envelope: " + listLine);

        // The other non-content shape: a -32601 whose method name alone is 4000 characters.
        var hugeMethod = new string('x', 4000);
        await server.SendRawLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":" + methodId + ",\"method\":\"" + hugeMethod + "\",\"params\":{}}");
        var methodLine = await server.ReadRawLineAsync(ResponseWatchdog);
        AssertWholeFrame(methodLine, methodId, budget, floor);
        Assert.DoesNotContain(hugeMethod, methodLine, StringComparison.Ordinal);
        McpAssert.ProtocolError(ServerProcess.JsonDocumentParse(methodLine), -32603);

        // Three replacements later the session still answers, and the ping frame is bounded
        // by the same rule as every other frame.
        await server.SendRawLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + pingId + ",\"method\":\"ping\",\"params\":{}}");
        var pingLine = await server.ReadRawLineAsync(ResponseWatchdog);
        AssertWholeFrame(pingLine, pingId, budget, floor);
    }

    // ---- helpers ----

    /// <summary>
    /// One <c>tools/call</c> frame with a fixed request id, so the length of the minimal
    /// refusal frame is a constant of the test instead of a function of the handshake.
    /// </summary>
    private static string ToolCallFrame(int id, string tool, object arguments) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":"
        + JsonSerializer.Serialize(tool) + ",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}";

    /// <summary>
    /// The shortest frame this transport emits: a bounded JSON-RPC error that still carries
    /// the request id. It is the floor below which no budget can be respected.
    /// </summary>
    private static string MinimalFrame(int id) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32603,\"message\":\"Response exceeds maxResponseChars\"}}";

    /// <summary>
    /// The invariants every delivered line must satisfy at any budget: it parses as one
    /// complete JSON document (a truncated frame cannot), it carries exactly this request id
    /// (an unattributed replacement would be lost to the client), its two possible shapes
    /// are mutually exclusive, and above the measured floor it respects the budget.
    /// </summary>
    private static void AssertWholeFrame(string line, int id, int budget, int floor)
    {
        var frame = ServerProcess.JsonDocumentParse(line);
        Assert.Equal(id, frame.GetProperty("id").GetInt32());
        Assert.True(frame.TryGetProperty("result", out _) ^ frame.TryGetProperty("error", out _),
            "A frame must carry exactly one of result and error: " + line);
        Assert.True(line.Length <= Math.Max(budget, floor),
            $"The delivered frame is {line.Length} characters, above both maxResponseChars={budget} and the floor {floor}.");
        if (budget >= floor)
        {
            Assert.True(line.Length <= budget,
                $"maxResponseChars={budget} is at or above the {floor} character floor, so the frame must fit; it is {line.Length} characters.");
        }
    }

    /// <summary>One <c>tools/call create_file</c> frame, built byte for byte by the test.</summary>
    private static string CreateFileFrame(int id, string path, string content) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"create_file\",\"arguments\":{\"path\":"
        + JsonSerializer.Serialize(path) + ",\"content\":" + JsonSerializer.Serialize(content) + "}}}";

    /// <summary>
    /// A frame whose UTF-8 size is exactly <paramref name="bytes"/>. The filler is one
    /// byte per character, and the resulting size is asserted rather than assumed, so the
    /// boundary case cannot silently drift away from the budget.
    /// </summary>
    private static string CreateFileFrameOfSize(int id, string path, int bytes, out string content)
    {
        var overhead = Encoding.UTF8.GetByteCount(CreateFileFrame(id, path, string.Empty));
        var filler = bytes - overhead;
        Assert.True(filler > 0, $"a frame of {bytes} bytes cannot carry the create_file envelope ({overhead} bytes)");
        content = new string('x', filler);
        var frame = CreateFileFrame(id, path, content);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(frame));
        return frame;
    }

    /// <summary>
    /// A frame the transport refused carries no request id: the refusal must not be
    /// attributed to a request, and the answer must not invent one. The FS-07 contract
    /// (docs/07: <c>-32600 с id=null</c>) is an explicit null member, not an omitted one:
    /// a client matches an error to "no id" by that member, and the same shape is required
    /// for the parse error of FS-13.
    /// </summary>
    private static void AssertUnattributed(JsonElement response)
    {
        Assert.True(response.TryGetProperty("id", out var id),
            "the response to an unattributable frame must carry an explicit \"id\":null, but the member is missing: " + response);
        Assert.Equal(JsonValueKind.Null, id.ValueKind);
    }

    /// <summary>
    /// Lets a server-side deadline fire while the invocation that owns it is parked in the
    /// host barrier. The wait is bounded and is never used to order two operations: the
    /// deadline is a timer inside the server process, and while the tool body is parked no
    /// frame can report that it fired.
    /// </summary>
    private static async Task LetDeadlineElapseAsync(long milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds + (long)DeadlineTimerSlack.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            await Task.Delay(25);
        }
    }
}
