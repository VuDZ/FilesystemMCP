using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-05")]
public sealed class ToolErrorsTests
{
    // ---- the four original cases: corrected behaviour, now Baseline ----

    [Fact, Trait("Status", "Baseline")]
    public async Task MissingFileHasOperationalCode()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { path = "missing.txt" });
        McpAssert.OperationalError(reply, "file_not_found", retryable: false);
        var payload = McpAssert.ErrorPayload(reply);
        Assert.Equal("missing.txt", payload.GetProperty("details").GetProperty("requested").GetString());
        McpAssert.NoSensitiveContent(reply, sandbox.Root, "System.IO");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task StaleHashHasConflictCodeAndDoesNotWrite()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "current");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("replace_in_file", new
        {
            path = "file.txt", target_snippet = "current", replacement_snippet = "new",
            original_hash = FileTextHelper.ComputeContentHashes("old").Sha256
        });
        Assert.Equal("current", await File.ReadAllTextAsync(path));
        McpAssert.OperationalError(reply, "hash_conflict", retryable: false);
        // Documented message contract (FS-05): a conflict sends the client back to
        // read_file. This is the canonical product text, not an exception message.
        var message = McpAssert.ErrorPayload(reply).GetProperty("message").GetString()!;
        Assert.Contains("read", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("again", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task UnknownToolIsInvalidParams()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("not_a_tool", new { });
        McpAssert.ProtocolError(reply, -32602);
        Assert.False(reply.TryGetProperty("result", out _), "Invalid params must stay a JSON-RPC error: " + reply);
        Assert.False(string.IsNullOrWhiteSpace(reply.GetProperty("error").GetProperty("message").GetString()));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task MissingRequiredArgumentIsInvalidParams()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { });
        McpAssert.ProtocolError(reply, -32602);
        Assert.False(reply.TryGetProperty("result", out _), "Invalid arguments must stay a JSON-RPC error: " + reply);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task WrongArgumentTypeIsInvalidParams()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        McpAssert.ProtocolError(await server.ToolAsync("read_file", new { path = 42 }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("read_file", new { path = "file.txt", start_line = "first" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("create_file", new { path = "new.txt" }), -32602);
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "new.txt")));
    }

    // ---- code matrix ----

    [Fact, Trait("Status", "Baseline")]
    public async Task MissingContainingDirectoryIsDistinctFromMissingFile()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var missingFile = await server.ToolAsync("read_file", new { path = "missing.txt" });
        var missingDirectory = await server.ToolAsync("read_file", new { path = "missing-dir/nested.txt" });
        McpAssert.OperationalError(missingFile, "file_not_found", retryable: false);
        McpAssert.OperationalError(missingDirectory, "directory_not_found", retryable: false);
        Assert.NotEqual(
            McpAssert.ErrorPayload(missingFile).GetProperty("code").GetString(),
            McpAssert.ErrorPayload(missingDirectory).GetProperty("code").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task TargetSnippetNotFoundIsOperationalCode()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "current");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("replace_in_file", new
        {
            path = "file.txt", target_snippet = "absent", replacement_snippet = "new",
            original_hash = FileTextHelper.ComputeContentHashes("current").Sha256
        });
        McpAssert.OperationalError(reply, "target_not_found", retryable: false);
        Assert.Equal("current", await File.ReadAllTextAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ExistingFileIsOperationalCode()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("existing.txt", "valuable");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("create_file", new { path = "existing.txt", content = "replacement" });
        McpAssert.OperationalError(reply, "file_exists", retryable: false);
        Assert.Equal("valuable", await File.ReadAllTextAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task UnsupportedEncodingIsOperationalCode()
    {
        using var sandbox = new Sandbox();
        // 0xC3 starts a two-byte sequence that 0x28 cannot complete: invalid UTF-8.
        await File.WriteAllBytesAsync(Path.Combine(sandbox.Workspace, "invalid.txt"), [0xC3, 0x28, 0x41]);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        McpAssert.OperationalError(
            await server.ToolAsync("read_file", new { path = "invalid.txt" }), "unsupported_encoding", retryable: false);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task BinaryFileIsOperationalCodeWithoutContent()
    {
        using var sandbox = new Sandbox();
        const string secret = "TOP_SECRET_FS05_FILE_CONTENT";
        await File.WriteAllBytesAsync(
            Path.Combine(sandbox.Workspace, "binary.bin"),
            [.. Encoding.UTF8.GetBytes(secret), 0x00]);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { path = "binary.bin" });
        McpAssert.OperationalError(reply, "binary_file", retryable: false);
        McpAssert.NoSensitiveContent(reply, secret);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task OversizedReadIsResourceLimitAndNotRetryable()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("large.txt", string.Join("\n", Enumerable.Repeat("line", FileService.DefaultMaxLines + 1)));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { path = "large.txt" });
        // An existing read guard, not a budget engine: the same request must not be retried.
        McpAssert.OperationalError(reply, "resource_limit", retryable: false);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task PathologicalRegexIsResourceLimitAndNotADefect()
    {
        using var sandbox = new Sandbox();
        // Classic nested-quantifier blow-up. The 1-second per-match deadline in SearchTool
        // is an existing guard, so exhausting it is an operational refusal the client can
        // act on (simplify the pattern), not a server defect.
        sandbox.Write("input.txt", new string('a', 32) + "!");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("search", new { regex = "(a+)+$", file_mask = "*.txt" });
        McpAssert.OperationalError(reply, "resource_limit", retryable: false);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task PathOutsideWorkspaceHidesAbsolutePaths()
    {
        using var sandbox = new Sandbox();
        var outside = Path.Combine(sandbox.Outside, "secret.txt");
        await File.WriteAllTextAsync(outside, "outside");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);

        var absolute = await server.ToolAsync("read_file", new { path = outside });
        McpAssert.OperationalError(absolute, "path_outside_workspace", retryable: false);
        McpAssert.NoSensitiveContent(absolute, sandbox.Root, sandbox.Outside, "secret.txt");
        Assert.False(McpAssert.ErrorPayload(absolute).GetProperty("details").TryGetProperty("requested", out _),
            "An absolute requested path must not be echoed: " + absolute);

        // A relative request is safe to report and is echoed as sent.
        var relative = await server.ToolAsync("read_file", new { path = "../outside/secret.txt" });
        McpAssert.OperationalError(relative, "path_outside_workspace", retryable: false);
        Assert.Equal("../outside/secret.txt",
            McpAssert.ErrorPayload(relative).GetProperty("details").GetProperty("requested").GetString());
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task SymlinkNotAllowedIsOperationalCode()
    {
        using var sandbox = new Sandbox();
        await sandbox.JunctionAsync("external", sandbox.Outside);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=false"]);
        McpAssert.OperationalError(
            await server.ToolAsync("read_file", new { path = "external/file.txt" }), "symlink_not_allowed", retryable: false);
    }

    [WindowsFact, Trait("Status", "Baseline"), SupportedOSPlatform("windows")]
    public async Task LockedAndAccessDeniedAreDistinctOperationalCodes()
    {
        using var sandbox = new Sandbox();
        var lockedPath = sandbox.Write("locked.txt", "content");
        var deniedPath = sandbox.Write("denied.txt", "content");
        var security = new FileInfo(deniedPath).GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        new FileInfo(deniedPath).SetAccessControl(security);
        try
        {
            using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await using var server = await ServerProcess.StartAsync(sandbox.Workspace);

            var lockedReply = await server.ToolAsync("read_file", new { path = "locked.txt" });
            var deniedReply = await server.ToolAsync("read_file", new { path = "denied.txt" });
            McpAssert.OperationalError(lockedReply, "file_locked", retryable: true);
            McpAssert.OperationalError(deniedReply, "access_denied", retryable: false);

            // The two refusals must stay distinguishable in text as well: a possibly
            // temporary lock versus a permission problem.
            var lockedMessage = McpAssert.ErrorPayload(lockedReply).GetProperty("message").GetString()!;
            var deniedMessage = McpAssert.ErrorPayload(deniedReply).GetProperty("message").GetString()!;
            Assert.NotEqual(lockedMessage, deniedMessage);
            Assert.Contains("temporar", lockedMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("denied", deniedMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            new FileInfo(deniedPath).SetAccessControl(security);
        }
    }

    [Fact, Trait("Status", "Baseline")]
    public void CancellationIsOperationalCode()
    {
        // The dispatcher has no cancellation source yet (FS-07 owns request cancellation),
        // so reachability of `cancelled` is asserted on the shared mapping that every
        // entry point uses. Only the exception type decides, never message text.
        var error = ToolErrorMapper.Map(new OperationCanceledException());
        Assert.NotNull(error);
        Assert.Equal("cancelled", error!.Code);
        Assert.False(error.Details!.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal("cancelled", ToolErrorMapper.Map(new TaskCanceledException())!.Code);
        Assert.Null(ToolErrorMapper.Map(new InvalidOperationException("boom")));
    }

    [Fact, Trait("Status", "Baseline")]
    public void EverySpecCodeIsMappedWithAStableShape()
    {
        var cases = new (string Code, Exception Failure)[]
        {
            ("file_not_found", new FileNotFoundException("missing")),
            ("directory_not_found", new DirectoryNotFoundException("no such directory")),
            ("file_locked", new MutationException("file_locked", "locked")),
            ("access_denied", new UnauthorizedAccessException("denied")),
            ("path_outside_workspace", PathPolicy.Error("path_outside_workspace")),
            ("symlink_not_allowed", PathPolicy.Error("symlink_not_allowed")),
            ("hash_conflict", MutationException.Conflict()),
            ("target_not_found", new MutationException("target_not_found", "target")),
            ("file_exists", MutationException.Exists()),
            ("unsupported_encoding", new MutationException("unsupported_encoding", "encoding")),
            ("binary_file", new MutationException("binary_file", "binary")),
            ("resource_limit", new OperationalException("resource_limit", "limit")),
            ("cancelled", new OperationCanceledException()),
        };
        Assert.Equal(13, cases.Length);
        Assert.Equal(cases.Length, cases.Select(item => item.Code).Distinct(StringComparer.Ordinal).Count());
        foreach (var (code, failure) in cases)
        {
            var error = ToolErrorMapper.Map(failure, "file.txt");
            Assert.NotNull(error);
            Assert.Equal(code, error!.Code);
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            // Only a transient lock may advise a retry.
            Assert.Equal(code == "file_locked", error.Details!.Retryable);
            Assert.Equal("file.txt", error.Details.Requested);
        }
    }

    [Fact, Trait("Status", "Baseline")]
    public void WindowsHResultTableSeparatesFileDirectoryAndLock()
    {
        // Filesystem-specific HResults, classified without touching the message text.
        Assert.Equal("file_not_found", FileErrorClassifier.ClassifyForPlatform(new IOException("missing", unchecked((int)0x80070002)), isWindows: true));
        Assert.Equal("directory_not_found", FileErrorClassifier.ClassifyForPlatform(new IOException("path", unchecked((int)0x80070003)), isWindows: true));
        Assert.Equal("file_locked", FileErrorClassifier.ClassifyForPlatform(new IOException("locked", unchecked((int)0x80070020)), isWindows: true));
        Assert.Equal("access_denied", FileErrorClassifier.ClassifyForPlatform(new IOException("denied", unchecked((int)0x80070005)), isWindows: true));
        // .NET reports a missing path component as DirectoryNotFoundException on both platforms.
        Assert.Equal("directory_not_found", FileErrorClassifier.ClassifyForPlatform(new DirectoryNotFoundException("component"), isWindows: true));
        // The Win32 table must never be applied to Unix errno (32 is EPIPE, 3 is ESRCH).
        Assert.Null(FileErrorClassifier.ClassifyForPlatform(new IOException("locked", unchecked((int)0x80070020)), isWindows: false));
        Assert.Null(FileErrorClassifier.ClassifyForPlatform(new IOException("path", unchecked((int)0x80070003)), isWindows: false));
    }

    // ---- details: bounded, sanitized, never sensitive ----

    [Fact, Trait("Status", "Baseline")]
    public void DetailsRequestedPathIsBoundedAndRelativeOnly()
    {
        var longRelative = string.Join("/", Enumerable.Repeat("directory", 100));
        var bounded = ToolErrorMapper.Map(new FileNotFoundException("missing"), longRelative)!.Details!.Requested!;
        Assert.True(bounded.Length <= ToolErrorMapper.MaxRequestedPathLength + 24, "requested path was not bounded: " + bounded.Length);
        Assert.Contains("truncated", bounded, StringComparison.Ordinal);

        // Absolute paths, including workspace-internal ones, are never echoed.
        Assert.Null(ToolErrorMapper.Map(new FileNotFoundException("missing"), Path.Combine(Path.GetTempPath(), "x.txt"))!.Details!.Requested);
        // Control characters cannot forge extra lines or JSON structure.
        var sanitized = ToolErrorMapper.Map(new FileNotFoundException("missing"), "a\nb\tc")!.Details!.Requested!;
        Assert.DoesNotContain("\n", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", sanitized, StringComparison.Ordinal);
    }

    [Fact, Trait("Status", "Baseline")]
    public void PlatformExceptionTextIsNeverRenderedToTheClient()
    {
        var failure = new DirectoryNotFoundException("Could not find a part of the path 'C:\\external\\private\\secret.txt'.");
        var error = ToolErrorMapper.Map(failure, "absent/file.txt")!;
        Assert.Equal("directory_not_found", error.Code);
        Assert.DoesNotContain("external", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Status", "Baseline")]
    public void ErrorObjectUsesSourceGeneratedSerialization()
    {
        // Reflection-based serialization is disabled in the product (AOT), so the error
        // object must round-trip through the source-generated context with a fixed shape.
        var error = ToolErrorMapper.Map(new OperationCanceledException(), "file.txt");
        var json = JsonSerializer.Serialize(error, McpJsonContext.Default.ToolOperationError);
        Assert.Equal(
            "{\"code\":\"cancelled\",\"message\":\"The operation was cancelled.\",\"details\":{\"requested\":\"file.txt\",\"retryable\":false}}",
            json);
        Assert.DoesNotContain(
            "requested",
            JsonSerializer.Serialize(ToolErrorMapper.Map(new OperationCanceledException()), McpJsonContext.Default.ToolOperationError),
            StringComparison.Ordinal);
    }

    // ---- unexpected defect: neutral message, correlation id, best-effort log ----

    [Fact, Trait("Status", "Baseline")]
    public async Task UnexpectedDefectIsCorrelatedAndOnlyLoggedInFull()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("correlation-server", protectedHost: true);
        // FS-06: the default is a per-user directory, so the test names the log directory
        // explicitly instead of relying on a folder next to the binary.
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        sandbox.Write("file.txt", "content");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);
        // list_directory on a file fails with ERROR_DIRECTORY, which the classifier
        // deliberately leaves unrecognized (FS-04 pins that). This is the foreseeable
        // client-mistake route into the -32603 bucket; InjectedFaultIsCorrelated below
        // covers the same guarantee for a genuinely unexpected defect.
        var reply = await server.ToolAsync("list_directory", new { path = "file.txt" });
        McpAssert.ProtocolError(reply, -32603);
        var error = reply.GetProperty("error");
        Assert.Equal("Internal error", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("result", out _), "Unexpected defect must not fake a tool result: " + reply);
        var correlationId = error.GetProperty("data").GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        McpAssert.NoSensitiveContent(reply, sandbox.Root, "ERROR_DIRECTORY", "DirectoryNotFoundException");

        // The full exception is written to the best-effort log under the same correlation id.
        // The writer still holds the file open, so the reader must share it.
        var log = await WaitForLogAsync(logPath, correlationId!);
        Assert.Contains(correlationId!, log, StringComparison.Ordinal);
        Assert.Contains("System.IO", log, StringComparison.Ordinal);
        Assert.Contains("   at ", log, StringComparison.Ordinal);
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    private static async Task<string> WaitForLogAsync(string directory, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var log = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            log = ReadLogDirectory(directory);
            if (log.Contains(expected, StringComparison.Ordinal))
            {
                return log;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for '{expected}' in '{directory}'. Observed: {log}");
        return log;
    }

    /// <summary>Reads a live log file with the sharing the sink grants its readers.</summary>
    private static string ReadLogDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            builder.Append(reader.ReadToEnd()).Append('\n');
        }

        return builder.ToString();
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task InjectedFaultIsCorrelatedAndLeavesNoPartialWrite()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        // The protected host reports atomic-write points over the FS-02 named-pipe barrier.
        // Releasing it with a command the host rejects makes it throw an IOException from
        // the harness: an environmental defect no client action could cause, which must
        // reach the correlation bucket instead of being guessed into an operational code.
        await using var gate = new ProcessGate("BeforeCommit");
        await using var server = await gate.Start(sandbox.Workspace);
        var pending = server.ToolAsync("replace_in_file", new
        {
            path = "file.txt", target_snippet = "original", replacement_snippet = "changed",
            original_hash = FileTextHelper.ComputeContentHashes("original").Sha256
        });
        await gate.At("BeforeCommit");
        await gate.Abort();

        var reply = await pending;
        McpAssert.ProtocolError(reply, -32603);
        var error = reply.GetProperty("error");
        Assert.Equal("Internal error", error.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("data").GetProperty("correlationId").GetString()));
        // The injected text stays in the log; the client sees only the neutral message.
        McpAssert.NoSensitiveContent(reply, sandbox.Root, "barrier", "IOException", "System.IO");
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public void SensitiveContentCheckRejectsEscapedAbsolutePaths()
    {
        const string leaked = @"C:\external\private\secret.txt";
        var payload = ServerProcess.JsonDocumentParse(
            "{\"code\":\"file_not_found\",\"message\":\"missing\",\"details\":{\"requested\":"
            + JsonSerializer.Serialize(leaked) + "}}");
        var response = ServerProcess.JsonDocumentParse(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":"
            + JsonSerializer.Serialize(payload.GetRawText()) + "}]}}");
        // The payload travels as escaped JSON text, so a check that only looked at the outer
        // frame would silently pass here; this guards the guard.
        Assert.ThrowsAny<Exception>(() => McpAssert.NoSensitiveContent(response, leaked));
        Assert.ThrowsAny<Exception>(() => McpAssert.NoSensitiveContent(response, "private"));
        McpAssert.NoSensitiveContent(response, "TOP_SECRET_absent_from_the_payload");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task LoggerUnavailableStillProducesTheResponse()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("blocked-logger-server", protectedHost: true);
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        // The configured log directory name is occupied by a file. The default sink is
        // the user directory, so the server must be pointed at this path explicitly.
        await File.WriteAllTextAsync(logPath, "fixture occupying directory name");
        sandbox.Write("file.txt", "content");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);

        var reply = await server.ToolAsync("list_directory", new { path = "file.txt" });
        McpAssert.ProtocolError(reply, -32603);
        var error = reply.GetProperty("error");
        Assert.Equal("Internal error", error.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("data").GetProperty("correlationId").GetString()));
        McpAssert.NoSensitiveContent(reply, sandbox.Root, "DirectoryNotFoundException", "System.IO");

        // Transport, tools and the log target survive: nothing is leaked or duplicated.
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
        var create = await server.ToolAsync("create_file", new { path = "created.txt", content = "valuable" });
        McpAssert.Success(create);
        Assert.Equal("valuable", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "created.txt")));
        Assert.True(File.Exists(logPath), "The occupied log path must stay untouched.");
        Assert.Equal("fixture occupying directory name", await File.ReadAllTextAsync(logPath));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task OneRequestProducesExactlyOneResponse()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "content");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        // Sent back to back: a duplicated answer to the first request would be read
        // here with the wrong id instead of the second request's reply.
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":501,\"method\":\"tools/call\",\"params\":{\"name\":\"list_directory\",\"arguments\":{\"path\":\"file.txt\"}}}");
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":502,\"method\":\"ping\",\"params\":{}}");
        var first = await server.ReadAsync();
        Assert.Equal(501, first.GetProperty("id").GetInt32());
        Assert.Equal(-32603, first.GetProperty("error").GetProperty("code").GetInt32());
        var second = await server.ReadAsync();
        Assert.Equal(502, second.GetProperty("id").GetInt32());
        Assert.True(second.TryGetProperty("result", out _), "Second request must be answered, not a duplicate: " + second);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SuccessAndFailureKeepTheSameEnvelopeShape()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "content");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);

        var success = await server.ToolAsync("read_file", new { path = "file.txt" });
        McpAssert.Success(success);
        var successPayload = ServerProcess.Payload(success);
        Assert.False(successPayload.TryGetProperty("code", out _), "Success payload must not look like an error: " + success);
        Assert.Equal("content", successPayload.GetProperty("text").GetString());

        var failure = await server.ToolAsync("read_file", new { path = "missing.txt" });
        McpAssert.ToolError(failure, "file_not_found");
        Assert.True(McpAssert.ErrorPayload(failure).GetProperty("code").ValueKind == JsonValueKind.String);
    }

    // ---- the legacy direct RPC surface uses the same mapping ----

    [Fact, Trait("Status", "Baseline")]
    public async Task LegacyEntryPointUsesTheSameMachineCode()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        // Legacy methods keep the accepted FS-01/FS-04 envelope (-32001 with the code as
        // the message) but their code set is the one shared mapping; FS-12 removes them.
        var reply = await server.CallAsync("read_file", new { path = "missing.txt" });
        Assert.Equal(-32001, reply.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("file_not_found", reply.GetProperty("error").GetProperty("message").GetString());
    }
}
