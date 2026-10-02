using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

/// <summary>
/// FS-06: diagnostics are best effort. No stage of the logger may throw into a tool or
/// the transport, stdout stays JSON-RPC only, content never reaches a log, and an
/// unavailable sink never turns a committed mutation into a failure.
/// </summary>
[Trait("Spec", "FS-06"), Trait("Status", "Baseline")]
public sealed class LoggingTests
{
    private const string Secret = "TOP_SECRET_FS06_do_not_log";

    // ---- live stdio process scope ----

    [Fact]
    public async Task UnavailableLogDirectoryDoesNotFailCreateOrTerminateServer()
    {
        using var sandbox = new Sandbox();
        // Deliberate entry-point failures always use the protected managed host,
        // even when other tests target an externally published binary.
        var executable = sandbox.CopyServer("blocked-log-server", protectedHost: true);
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        await File.WriteAllTextAsync(logPath, "fixture occupying directory name");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);
        var response = await server.ToolAsync("create_file", new { path = "created.txt", content = "valuable" });
        McpAssert.Success(response);
        Assert.Equal("valuable", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "created.txt")));
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
        // The occupied name stays untouched: the sink is disabled, nothing is clobbered.
        Assert.False(Directory.Exists(logPath));
        Assert.Equal("fixture occupying directory name", await File.ReadAllTextAsync(logPath));
        // The fallback is reported once, not per record.
        Assert.True(await WaitForAsync(
            () => CountReports(server.Stderr) >= 1,
            TimeSpan.FromSeconds(5)), "expected one safe fallback report on stderr: " + server.Stderr);
    }

    [Fact]
    public async Task ContentSecretDoesNotAppearInDiagnostics()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("secret-log-server", protectedHost: true);
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);
        var reply = await server.ToolAsync("create_file", new { path = "secret.txt", content = Secret });
        McpAssert.Success(reply);
        await server.CallAsync("ping", new { });

        var logs = await WaitForLogsAsync(logPath, "Tool invoke: create_file");
        Assert.Contains("secret.txt", logs);
        Assert.DoesNotContain(Secret, logs);
        Assert.DoesNotContain(Secret, server.Stderr);
        // The redaction is explicit, not an accident of truncation.
        Assert.Contains("<redacted", logs);
    }

    [WindowsFact, SupportedOSPlatform("windows")]
    public async Task DeniedLogDirectoryKeepsServerServingRequests()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("acl-log-server", protectedHost: true);
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        Directory.CreateDirectory(logPath);
        var security = new DirectoryInfo(logPath).GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.CreateFiles,
            AccessControlType.Deny);
        security.AddAccessRule(deny);
        new DirectoryInfo(logPath).SetAccessControl(security);
        try
        {
            await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);
            var response = await server.ToolAsync("create_file", new { path = "served.txt", content = "served" });
            McpAssert.Success(response);
            Assert.Equal("served", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "served.txt")));
            // The denied sink is reported once and then stays quiet.
            Assert.True(await WaitForAsync(
                () => CountReports(server.Stderr) >= 1,
                TimeSpan.FromSeconds(5)), "expected one safe fallback report on stderr: " + server.Stderr);
            var afterFirst = CountReports(server.Stderr);
            await server.CallAsync("ping", new { });
            await server.ToolAsync("create_file", new { path = "served2.txt", content = "served2" });
            Assert.False(await WaitForAsync(
                () => CountReports(server.Stderr) > afterFirst,
                TimeSpan.FromMilliseconds(300)), "the disabled sink reported more than once: " + server.Stderr);
            Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            new DirectoryInfo(logPath).SetAccessControl(security);
        }
    }

    [WindowsFact, SupportedOSPlatform("windows")]
    public async Task DeniedLogFileKeepsPriorBytesAndReportsOnce()
    {
        var directory = NewTempDirectory("fs06-denied-file");
        var stderr = new StringWriter();
        var filePath = Path.Combine(directory, Logging.SessionFileName(DateTimeOffset.UtcNow, Environment.ProcessId));
        const string prior = "prior-session-bytes";
        FileSystemAccessRule? deny = null;
        try
        {
            File.WriteAllText(filePath, prior);
            var security = new FileInfo(filePath).GetAccessControl(AccessControlSections.Access);
            deny = new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.WriteData | FileSystemRights.AppendData,
                AccessControlType.Deny);
            security.AddAccessRule(deny);
            new FileInfo(filePath).SetAccessControl(security);

            McpLogger.ResetForTests();
            McpLogger.ErrorWriter = stderr;
            // Real Start path: the session name is computed inside LogFileSink, not passed in.
            McpLogger.Start(directory);

            Assert.False(McpLogger.IsFileSinkEnabled);
            Assert.Equal(prior, ReadShared(filePath));
            Assert.Equal(1, CountReports(stderr.ToString()));

            using var sandbox = new Sandbox();
            var registry = new ToolRegistry();
            registry.Register(new CreateFileTool(sandbox.Workspace));
            var created = await registry.ExecuteToolAsync(
                "create_file",
                ServerProcess.Arguments(new { path = "created.txt", content = "valuable" }));
            Assert.Equal("success", ServerProcess.JsonDocumentParse(created).GetProperty("status").GetString());
            Assert.Equal("valuable", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "created.txt")));
            Assert.Equal(prior, ReadShared(filePath));

            McpLogger.Info("after-denied-file");
            Poll(
                () => StderrLines(stderr).Any(line => line.Contains("after-denied-file", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                "diagnostics to continue on stderr after the session file was denied. Stderr=[" + stderr + "]");
            Assert.Equal(1, CountReports(stderr.ToString()));
            Assert.Equal(prior, ReadShared(filePath));
        }
        finally
        {
            McpLogger.ResetForTests();
            if (deny is not null && File.Exists(filePath))
            {
                try
                {
                    var security = new FileInfo(filePath).GetAccessControl(AccessControlSections.Access);
                    security.RemoveAccessRuleSpecific(deny);
                    new FileInfo(filePath).SetAccessControl(security);
                }
                catch (UnauthorizedAccessException)
                {
                    // Temp cleanup is best effort.
                }
            }

            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SimultaneousRequestsProduceOneValidFrameEachAndNoContentInLogs()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("simultaneous-log-server", protectedHost: true);
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);

        const int count = 20;
        for (var i = 0; i < count; i++)
        {
            var id = await server.SendToolAsync("create_file", new { path = $"flood-{i}.txt", content = Secret + i });
            var response = await server.ReadResponseAsync(id);
            Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
            Assert.False(response.TryGetProperty("error", out _), "unexpected protocol error: " + response);
            Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean());
        }

        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
        Assert.Equal(count, Directory.EnumerateFiles(sandbox.Workspace, "flood-*.txt").Count());

        var logs = await WaitForLogsAsync(logPath, "Tool done: create_file");
        Assert.DoesNotContain(Secret, logs + server.Stderr);
    }

    [Fact]
    public async Task BoundedExitFlushesQueuedRecords()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("bounded-exit-server", protectedHost: true);
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);
        await using (server)
        {
            await server.ToolAsync("create_file", new { path = "last.txt", content = "last" });
            await server.CallAsync("ping", new { });
        }

        // Disposing closes stdin. The process must leave on its own inside the 2s budget:
        // exit code 0, and the watchdog must not have killed it. Per-line flush stays;
        // a hung Shutdown would still have the tool record on disk and is a failure.
        Assert.Equal(0, server.ExitCode);
        Assert.False(server.KilledByWatchdog);
        var logs = ReadLogs(logPath);
        Assert.Contains("last.txt", logs);
        Assert.Contains("Tool done: create_file", logs);
        Assert.Contains("[INFO]", logs);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\]", logs);
    }

    // ---- in-process logger scope ----

    [Fact]
    public void FormattingFailureDoesNotEscapeAndLaterRecordsStillFlow()
    {
        using var scope = new InProcessLogScope();
        McpLogger.FailNextWrite = new McpLogger.LogFailureInjection(
            McpLogger.FailureFormat,
            _ => throw new InvalidOperationException("injected format failure"));
        McpLogger.Error("first-record", new InvalidOperationException("payload"));
        // The injected failure cannot leave the logger: the record still reaches the file
        // and the failure is described there instead of escaping into the tool.
        scope.WaitForFile(line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        McpLogger.Info("second-record");
        scope.WaitForFile(line => line.Contains("second-record", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteFailureIsReportedOnceAndNeverPropagates()
    {
        var directory = NewTempDirectory("fs06-write-failure");
        var stderr = new StringWriter();
        try
        {
            McpLogger.ResetForTests();
            McpLogger.ErrorWriter = stderr;
            McpLogger.StartWith(new LogFileSink(directory, "mcplog-scope.log"), directory);
            Assert.True(McpLogger.IsFileSinkEnabled);

            // A writer-stage failure loses that record: the logger must count it, retire the
            // sink and report the fallback exactly once instead of failing the caller.
            McpLogger.FailNextWrite = new McpLogger.LogFailureInjection(
                McpLogger.FailureWrite,
                _ => throw new IOException("injected write failure"));
            McpLogger.Info("first-record");
            Poll(
                () => StderrLines(stderr).Any(line => line.Contains("File log disabled", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                "the retired sink to be reported on stderr. Stderr=[" + string.Join(" / ", StderrLines(stderr)) + "]");
            Assert.False(McpLogger.IsFileSinkEnabled);
            Assert.True(McpLogger.DroppedCount >= 1, "the lost record must be counted");
            var reports = StderrLines(stderr).Count(line => line.Contains("File log disabled", StringComparison.Ordinal));

            // The lost record never reached the file; later records go to stderr directly,
            // without repeating the report and without throwing into the caller.
            Assert.DoesNotContain("first-record", ReadShared(Path.Combine(directory, "mcplog-scope.log")));
            for (var i = 0; i < 5; i++)
            {
                McpLogger.Info("record-" + i);
            }

            Poll(
                () => StderrLines(stderr).Any(line => line.Contains("record-4", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                "diagnostics to continue on stderr after the sink failed");
            Assert.Equal(reports, StderrLines(stderr).Count(line => line.Contains("File log disabled", StringComparison.Ordinal)));
            Assert.Equal(1, reports);
        }
        finally
        {
            McpLogger.ResetForTests();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SinkInternalWriteFailureIsReportedOnceAndNeverPropagates()
    {
        var directory = NewTempDirectory("fs06-sink-write");
        var stderr = new StringWriter();
        try
        {
            var sink = new LogFileSink(directory, "mcplog-scope.log");
            // The throw happens inside LogFileSink.Write, so the sink's own catch runs.
            // This is not the pre-write FailureWrite injection.
            sink.FailNextOperation = () => throw new IOException("injected disk full");
            McpLogger.ResetForTests();
            McpLogger.ErrorWriter = stderr;
            McpLogger.StartWith(sink, directory);
            Assert.True(McpLogger.IsFileSinkEnabled);
            Assert.False(sink.IsFailed);

            McpLogger.Info("first-record");
            Poll(
                () => sink.IsFailed
                    && !McpLogger.IsFileSinkEnabled
                    && McpLogger.DroppedCount >= 1
                    && StderrLines(stderr).Any(line => line.Contains("File log disabled", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                "a failure inside the sink to count the loss and retire it. Stderr=["
                + string.Join(" / ", StderrLines(stderr)) + "] Dropped=" + McpLogger.DroppedCount);
            Assert.Null(sink.FailNextOperation);
            Assert.Equal(1, StderrLines(stderr).Count(line => line.Contains("File log disabled", StringComparison.Ordinal)));
            Assert.DoesNotContain("first-record", ReadShared(Path.Combine(directory, "mcplog-scope.log")));
            var dropped = McpLogger.DroppedCount;

            for (var i = 0; i < 5; i++)
            {
                McpLogger.Info("record-" + i);
            }

            Poll(
                () => StderrLines(stderr).Any(line => line.Contains("record-4", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                "diagnostics to continue on stderr after the sink failed internally");
            Assert.Equal(1, StderrLines(stderr).Count(line => line.Contains("File log disabled", StringComparison.Ordinal)));
            Assert.Equal(dropped, McpLogger.DroppedCount);
        }
        finally
        {
            McpLogger.ResetForTests();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void UnavailableDirectoryReportsOnceAndKeepsDiagnosticsOnStderr()
    {
        var directory = NewTempDirectory("fs06-unavailable");
        var occupied = Path.Combine(directory, "logs");
        var writer = new StringWriter();
        try
        {
            File.WriteAllText(occupied, "not a directory");
            McpLogger.ResetForTests();
            McpLogger.ErrorWriter = writer;
            McpLogger.StartWith(null, occupied);
            Assert.False(McpLogger.IsFileSinkEnabled);
            Assert.Null(McpLogger.ActiveLogFilePath);
            McpLogger.Info("after-disabled-sink");
            Poll(
                () => writer.ToString().Contains("after-disabled-sink", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                "diagnostics to continue on the stderr fallback");
            var lines = StderrLines(writer);
            Assert.Single(lines, line => line.Contains("File log disabled", StringComparison.Ordinal));
        }
        finally
        {
            McpLogger.ResetForTests();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void StderrUnavailableKeepsDiagnosticsSilentAndProcessAlive()
    {
        using var scope = new InProcessLogScope();
        McpLogger.ErrorWriter = new ThrowingWriter();
        McpLogger.FailNextWrite = new McpLogger.LogFailureInjection(McpLogger.FailureStderr);
        McpLogger.Error("first-record");
        McpLogger.Info("second-record");
        // A healthy file sink never consults stderr, so the record still lands in the
        // file and the stderr injection stays armed.
        scope.WaitForFile(line => line.Contains("second-record", StringComparison.Ordinal));
        scope.WaitForFile(line => line.Contains("first-record", StringComparison.Ordinal));
        Assert.Equal(McpLogger.FailureStderr, McpLogger.FailNextWrite?.Stage);
        Assert.Equal(0, McpLogger.DroppedCount);
    }

    [Fact]
    public void StderrBranchCountsTheLossAndKeepsTheWriterAlive()
    {
        var directory = NewTempDirectory("fs06-stderr-branch");
        var occupied = Path.Combine(directory, "logs");
        try
        {
            File.WriteAllText(occupied, "not a directory");
            McpLogger.ResetForTests();
            McpLogger.ErrorWriter = new ThrowingWriter();
            McpLogger.StartWith(null, occupied);
            Assert.False(McpLogger.IsFileSinkEnabled);

            McpLogger.FailNextWrite = new McpLogger.LogFailureInjection(McpLogger.FailureStderr);
            McpLogger.Info("first-stderr-record");
            Poll(
                () => McpLogger.FailNextWrite is null && McpLogger.DroppedCount >= 1,
                TimeSpan.FromSeconds(5),
                "the stderr injection to be consumed and the loss counted. Dropped=" + McpLogger.DroppedCount);

            // The writer is still alive: a later record is accepted and its loss is counted
            // when ErrorWriter itself throws.
            McpLogger.Info("second-stderr-record");
            Poll(
                () => McpLogger.DroppedCount >= 2,
                TimeSpan.FromSeconds(5),
                "the writer to accept a later record after stderr threw. Dropped=" + McpLogger.DroppedCount);
            Assert.Null(McpLogger.FailNextWrite);
        }
        finally
        {
            McpLogger.ResetForTests();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CompletionLogFailureAfterCommitDoesNotChangeCreateSuccess()
    {
        using var sandbox = new Sandbox();
        var logged = false;
        var registry = new ToolRegistry
        {
            CompletionLog = (_, _, success, _) =>
            {
                logged = true;
                Assert.True(success);
                throw new IOException("log sink full");
            }
        };
        registry.Register(new CreateFileTool(sandbox.Workspace));
        var result = await registry.ExecuteToolAsync(
            "create_file",
            ServerProcess.Arguments(new { path = "file.txt", content = "created\r\n" }));
        Assert.True(logged);
        Assert.Equal("success", ServerProcess.JsonDocumentParse(result).GetProperty("status").GetString());
        Assert.Equal(
            FileTextHelper.ComputeContentHashes("created\n").Sha256,
            ServerProcess.JsonDocumentParse(result).GetProperty("sha256").GetString());
        Assert.Equal("created\r\n", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "file.txt")));
    }

    [Fact]
    public async Task CompletionLogFailureDoesNotReplaceTheToolException()
    {
        var logged = false;
        var registry = new ToolRegistry
        {
            CompletionLog = (_, _, success, _) =>
            {
                logged = true;
                Assert.False(success);
                throw new InvalidOperationException("log failed");
            }
        };
        registry.Register(new ThrowingTool());
        var exception = await Assert.ThrowsAsync<IOException>(() => registry.ExecuteToolAsync(
            "throwing_tool",
            ServerProcess.Arguments(new { })));
        Assert.True(logged);
        Assert.Equal("original tool failure", exception.Message);
    }

    [Fact]
    public void QueueOverflowDropsRecordsWithoutBlockingTheCaller()
    {
        var queue = new BoundedLogQueue(maxEntries: 2, maxBytes: 4096, maxEntryBytes: 1024);
        Assert.True(queue.TryEnqueue("first"));
        Assert.True(queue.TryEnqueue("second"));
        Assert.Equal(2, queue.PendingCount);

        var started = Environment.TickCount64;
        for (var i = 0; i < 5000; i++)
        {
            queue.TryEnqueue("flood-record-" + i + new string('x', 200));
        }

        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
        // The bounded queue drops instead of blocking: a saturated buffer must never
        // become transport back-pressure.
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"enqueue blocked for {elapsed}.");
        Assert.True(queue.DroppedCount >= 4998, "expected every overflow record to be counted as dropped");
        Assert.Equal(2, queue.PendingCount);

        // The dropped records do not displace the ones already accepted.
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("first", first);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("second", second);
        Assert.False(queue.TryDequeue(out _));
        Assert.False(queue.DrainRemaining(new List<string>(), DateTimeOffset.UtcNow.AddSeconds(1)) > 0);
    }

    [Fact]
    public void FrameBoundQueueKeepsOneRecordPerLineAndBoundsEntryLength()
    {
        var queue = new BoundedLogQueue(maxEntries: 8, maxBytes: 1024 * 1024, maxEntryBytes: 128);
        var overlong = "x" + new string('y', 400);
        Assert.True(queue.TryEnqueue(overlong));
        Assert.True(queue.TryDequeue(out var line));
        Assert.True(line.Length <= 200, $"entry length {line.Length} exceeded the bound");
        Assert.Contains("truncated", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public async Task StdoutStaysReservedForProtocolFrames()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("stdout-log-server", protectedHost: true);
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        var isDll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var host = isDll ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : executable;
        var arguments = isDll
            ? new[] { executable, sandbox.Workspace, "--logDirectory=" + logPath }
            : [sandbox.Workspace, "--logDirectory=" + logPath];

        var result = await ProcessRunner.RunWithClosedInputAsync(host, arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.DoesNotContain("TOOL", result.Stdout);
        Assert.True(Directory.Exists(logPath), "the configured log directory should have been used");
    }

    [Fact]
    public async Task UnknownOrDuplicateLogDirectoryOptionFailsStartupWithoutStdout()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("option-log-server", protectedHost: true);
        var isDll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var host = isDll ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : executable;
        var directory = NewTempDirectory("fs06-option");
        try
        {
            foreach (var extra in new[]
            {
                new[] { "--logDirectory=" },
                new[] { "--logDirectory=   " },
                new[] { "--logDirectory=" + directory, "--logDirectory=" + directory },
                new[] { "--logDirectory=" + directory, "--unknownOption=1" }
            })
            {
                var arguments = isDll
                    ? new[] { executable, sandbox.Workspace }.Concat(extra).ToArray()
                    : new[] { sandbox.Workspace }.Concat(extra).ToArray();
                var result = await ProcessRunner.RunWithClosedInputAsync(host, arguments);
                Assert.Equal(1, result.ExitCode);
                Assert.Contains("Startup failed:", result.Stderr);
                Assert.Equal("", result.Stdout);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void UtcTimestampAndCorrelationUseOneFormatForInfoAndError()
    {
        using var scope = new InProcessLogScope();
        McpLogger.Info("info-record", correlationId: "req-1");
        McpLogger.Error("error-record", new InvalidOperationException("hidden platform message"), correlationId: "req-1");
        scope.WaitForFile(line => line.Contains("error-record", StringComparison.Ordinal));

        var info = scope.FileLines.First(line => line.Contains("info-record", StringComparison.Ordinal));
        var error = scope.FileLines.First(line => line.Contains("error-record", StringComparison.Ordinal));
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] \[INFO\] \[req=req-1\] ", info);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] \[ERROR\] \[req=req-1\] ", error);
        // The platform message is never logged (it can embed an absolute path)...
        Assert.DoesNotContain(scope.FileText, "hidden platform message");
        // ...but the type and the stack trace are.
        Assert.Contains(nameof(InvalidOperationException), scope.FileText);
    }

    [Fact]
    public void RotationIsBoundedAndKeepsForeignLogs()
    {
        var directory = NewTempDirectory("fs06-rotation");
        try
        {
            var foreign = Path.Combine(directory, "mcplog-19700101-pid999999.log");
            var unrelated = Path.Combine(directory, "other.log");
            File.WriteAllText(foreign, "foreign session");
            File.WriteAllText(unrelated, "unrelated");

            var baseName = Logging.SessionFileName(DateTimeOffset.UtcNow, Environment.ProcessId);
            var sink = new LogFileSink(directory, baseName, maxBytes: 1024, maxFilesPerSession: 5);
            try
            {
                for (var i = 0; i < 400; i++)
                {
                    sink.Write("record " + i + " padding padding padding padding padding padding");
                }
            }
            finally
            {
                sink.Close();
            }

            var own = Directory.EnumerateFiles(directory, baseName + "*").ToArray();
            Assert.Equal(5, own.Length);
            Assert.False(File.Exists(Path.Combine(directory, baseName + ".5")));
            Assert.Equal("foreign session", File.ReadAllText(foreign));
            Assert.Equal("unrelated", File.ReadAllText(unrelated));
            // Newest content survives; the oldest index was dropped by rotation.
            Assert.Contains("record 399", File.ReadAllText(Path.Combine(directory, baseName)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // ---- helpers ----

    /// <summary>Starts the real file sink in a private directory and restores global state on exit.</summary>
    private sealed class InProcessLogScope : IDisposable
    {
        private readonly StringWriter _stderr = new();

        internal InProcessLogScope()
        {
            Directory = NewTempDirectory("fs06-inprocess");
            FilePath = Path.Combine(Directory, "mcplog-scope.log");
            McpLogger.ResetForTests();
            McpLogger.ErrorWriter = _stderr;
            McpLogger.StartWith(new LogFileSink(Directory, "mcplog-scope.log"), Directory);
        }

        internal string Directory { get; }

        internal string FilePath { get; }

        internal string[] FileLines => FileText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        internal string FileText => ReadShared(FilePath);

        internal void WaitForFile(Func<string, bool> predicate) =>
            Poll(
                () => FileLines.Any(predicate),
                TimeSpan.FromSeconds(5),
                $"a record in '{FilePath}'. Enabled={McpLogger.IsFileSinkEnabled} " +
                $"Dropped={McpLogger.DroppedCount} Text=[{FileText}] " +
                $"Stderr=[{string.Join(" / ", StderrLines(_stderr))}]");

        internal void WaitForStderr(Func<string, bool> predicate) =>
            Poll(() => StderrLines(_stderr).Any(predicate), TimeSpan.FromSeconds(5), "a record on the stderr fallback");

        public void Dispose()
        {
            McpLogger.ResetForTests();
            DeleteDirectory(Directory);
        }
    }

    private static string[] StderrLines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static int CountReports(string stderr) =>
        stderr.Split("File log disabled", StringSplitOptions.None).Length - 1;

    private static string ReadLogs(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            builder.Append(ReadShared(file)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads a log the writer still holds open. A plain read opens with <c>FileShare.Read</c>
    /// and fails against a live writer, so the reader must ask for the same sharing the
    /// sink grants; the failure would otherwise look like missing diagnostics.
    /// </summary>
    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Waits for a diagnostic file to exist and to contain the expected record.</summary>
    private static async Task<string> WaitForLogsAsync(string directory, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var logs = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            logs = ReadLogs(directory);
            if (logs.Contains(expected, StringComparison.Ordinal))
            {
                return logs;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for '{expected}' in '{directory}'. Observed: {logs}");
        return logs;
    }

    private static string NewTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Temp cleanup is best effort.
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    /// <summary>Bounded wait for the background writer; never a bare sleep as synchronization.</summary>
    private static void Poll(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("Timed out waiting for " + what + ".");
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("stderr is unavailable");

        public override void Write(char value) => throw new IOException("stderr is unavailable");
    }

    private sealed class ThrowingTool : IMcpTool
    {
        public string Name => "throwing_tool";
        public string Description => "Fails before any completion log.";
        public string InputSchemaJson => "{}";

        public Task<string> ExecuteAsync(JsonElement arguments) =>
            Task.FromException<string>(new IOException("original tool failure"));
    }
}
