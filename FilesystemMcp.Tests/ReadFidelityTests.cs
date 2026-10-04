using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

/// <summary>
/// FS-10 acceptance: read_file returns exact canonical text — leading blank lines, the
/// terminal LF and the delimiter that closed the last returned line — whole-file hashes
/// regardless of the selection, and explicit completeness metadata
/// (total_lines/start_line/end_line/truncated/has_more). A line cap is reported as
/// truncated=true, a plain range request never is; max_lines without the explicit
/// opt-in is an argument error instead of a silently ignored value. Every case is
/// deterministic and every fixture lives in the temp sandbox.
/// </summary>
[Trait("Spec", "FS-10")]
public sealed class ReadFidelityTests
{
    // ---- full-file fidelity: leading blanks, terminal LF, no invented line ----------------

    [Theory, Trait("Status", "Baseline")]
    [InlineData("\n\nvalue\n")]
    [InlineData("value\n")]
    [InlineData("\n\n")]
    public async Task FullReadPreservesNormalizedTextIncludingBlankLinesAndTerminalLf(string content)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", content);
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new());
        Assert.Equal(content, reply.Text);
        Assert.Equal(FileTextHelper.ComputeContentHashes(content).Sha256, reply.Sha256);
        Assert.False(reply.Truncated);
        Assert.False(reply.HasMore);
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("\n", "\n", 1)]          // one empty line; the terminal LF is not a second line
    [InlineData("a\n", "a\n", 1)]
    [InlineData("a", "a", 1)]
    [InlineData("a\nb", "a\nb", 2)]
    [InlineData("a\n\n", "a\n\n", 2)]    // the second (empty) line is closed by the terminal LF
    public async Task FullReadCountsLinesWithoutInventingOneForTheTerminalLf(
        string stored, string expectedText, int totalLines)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", stored);
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new());
        Assert.Equal(expectedText, reply.Text);
        Assert.Equal(totalLines, reply.TotalLines);
        Assert.Equal(1, reply.StartLine);
        Assert.Equal(totalLines, reply.EndLine);
        Assert.False(reply.Truncated);
        Assert.False(reply.HasMore);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EmptyFileReturnsEmptyTextZeroLinesAndNoSelection()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("empty.txt", "");
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync("empty.txt", new());
        Assert.Equal("", reply.Text);
        Assert.Equal(0, reply.TotalLines);
        Assert.Null(reply.StartLine);
        Assert.Null(reply.EndLine);
        Assert.False(reply.Truncated);
        Assert.False(reply.HasMore);
        Assert.Equal(FileTextHelper.ComputeContentHashes("").Sha256, reply.Sha256);

        // Same shape over the wire: start_line/end_line are simply absent for an empty
        // selection — the contract expresses null, not a fabricated 1..0 range.
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var payload = ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "empty.txt" }));
        Assert.Equal("", payload.GetProperty("text").GetString());
        Assert.Equal(0, payload.GetProperty("total_lines").GetInt32());
        Assert.False(payload.GetProperty("truncated").GetBoolean());
        Assert.False(payload.GetProperty("has_more").GetBoolean());
        Assert.False(payload.TryGetProperty("start_line", out _), "empty selection must not report start_line: " + payload);
        Assert.False(payload.TryGetProperty("end_line", out _), "empty selection must not report end_line: " + payload);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task CrlfIsNormalizedToLfInFullTextAndRangeDelimiters()
    {
        using var sandbox = new Sandbox();
        const string stored = "a\r\nb\r\nc";
        sandbox.Write("file.txt", stored);
        var files = new FileOperationsService(sandbox.Workspace);

        var full = await files.ReadFileAsync("file.txt", new());
        Assert.Equal("a\nb\nc", full.Text);
        Assert.Equal(3, full.TotalLines);

        // Each selected line keeps the LF that closed it in the canonical text.
        Assert.Equal("a\n", (await files.ReadFileAsync("file.txt", new(StartLine: 1, EndLine: 1))).Text);
        var middle = await files.ReadFileAsync("file.txt", new(StartLine: 2, EndLine: 2));
        Assert.Equal("b\n", middle.Text);
        // The last line of a file without a terminal LF has no delimiter to return.
        Assert.Equal("c", (await files.ReadFileAsync("file.txt", new(StartLine: 3, EndLine: 3))).Text);
        Assert.Equal(FileTextHelper.ComputeContentHashes("a\nb\nc").Sha256, middle.Sha256);
    }

    // ---- range semantics -------------------------------------------------------------------

    [Theory, Trait("Status", "Baseline")]
    [InlineData("a\nb\nc\nd", 1, 1, "a\n")]             // interior line: its closing LF is returned
    [InlineData("a\nb\nc\nd", 4, 4, "d")]                // last line, file has no terminal LF
    [InlineData("a\nb\nc\nd\n", 4, 4, "d\n")]            // last line closed by the terminal LF
    [InlineData("a\nb\nc\nd\n", 1, 2, "a\nb\n")]
    [InlineData("first\n\nthird\nlast", 2, 2, "\n")]    // an empty selected line is real content
    public async Task RangeReturnsTheDelimiterThatClosedTheLastSelectedLine(
        string content, int startLine, int endLine, string expectedText)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", content);
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync(
            "file.txt", new(StartLine: startLine, EndLine: endLine));
        Assert.Equal(expectedText, reply.Text);
        Assert.Equal(startLine, reply.StartLine);
        Assert.Equal(endLine, reply.EndLine);
        Assert.False(reply.Truncated); // a range request is not a truncation
        Assert.Equal(FileTextHelper.ComputeContentHashes(content).Sha256, reply.Sha256);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task RangeKeepsInitialBlankLine()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "first\n\nthird\nlast");
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new(StartLine: 2, EndLine: 3));
        Assert.Equal("\nthird\n", reply.Text);
        Assert.Equal(2, reply.StartLine);
        Assert.Equal(3, reply.EndLine);
        Assert.Equal(4, reply.TotalLines);
        Assert.False(reply.Truncated);
        Assert.True(reply.HasMore); // "last" exists after the returned end_line
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task StartLineBeyondEofIsEmptySelectionWithMetadata()
    {
        using var sandbox = new Sandbox();
        const string content = "a\nb\nc\nd";
        sandbox.Write("file.txt", content);
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new(StartLine: 5, EndLine: 9));
        Assert.Equal("", reply.Text);
        Assert.Equal(4, reply.TotalLines);
        Assert.Null(reply.StartLine);
        Assert.Null(reply.EndLine);
        Assert.False(reply.Truncated);
        Assert.False(reply.HasMore);
        // The hashes still describe the whole file, selection or not.
        Assert.Equal(FileTextHelper.ComputeContentHashes(content).Sha256, reply.Sha256);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EndLineBeyondEofClampsToTheLastAvailableLine()
    {
        using var sandbox = new Sandbox();
        const string content = "first\n\nthird\nlast"; // no terminal LF
        sandbox.Write("file.txt", content);
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new(StartLine: 2, EndLine: 9));
        Assert.Equal("\nthird\nlast", reply.Text);
        Assert.Equal(2, reply.StartLine);
        Assert.Equal(4, reply.EndLine); // clamped, and the metadata reports the actual range
        Assert.False(reply.Truncated);
        Assert.False(reply.HasMore);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task RangeHashDescribesWholeNormalizedFile()
    {
        using var sandbox = new Sandbox();
        const string content = "a\r\nb\r\nc";
        sandbox.Write("file.txt", content);
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new(StartLine: 2, EndLine: 2));
        Assert.Equal(FileTextHelper.ComputeContentHashes("a\nb\nc").Sha256, reply.Sha256);
    }

    // ---- prefix cap and its metadata ---------------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task PrefixCapReportsTotalLinesTruncationAndFullFileHash()
    {
        using var sandbox = new Sandbox();
        const string content = "a\nb\nc\nd";
        sandbox.Write("file.txt", content);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var result = ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "file.txt", allow_large_read = true, max_lines = 2 }));
        Assert.True(result.TryGetProperty("truncated", out var truncated), "read_file did not report truncation: " + result);
        Assert.True(truncated.GetBoolean());
        Assert.True(result.GetProperty("has_more").GetBoolean());
        Assert.Equal(4, result.GetProperty("total_lines").GetInt32());
        Assert.Equal(1, result.GetProperty("start_line").GetInt32());
        Assert.Equal(2, result.GetProperty("end_line").GetInt32());
        Assert.Equal("a\nb\n", result.GetProperty("text").GetString());
        Assert.Equal(FileTextHelper.ComputeContentHashes(content).Sha256, result.GetProperty("sha256").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task MaxLinesExactlyTheFileSizeIsNotTruncated()
    {
        using var sandbox = new Sandbox();
        const string content = "a\nb\nc";
        sandbox.Write("file.txt", content);
        var reply = await new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new(AllowLargeRead: true, MaxLines: 3));
        Assert.Equal("a\nb\nc", reply.Text);
        Assert.Equal(3, reply.TotalLines);
        Assert.False(reply.Truncated);
        Assert.False(reply.HasMore);
    }

    // ---- default cap: exact cap / cap+1 -------------------------------------------------------

    [Theory, Trait("Status", "Baseline")]
    [InlineData(FileOperationsService.DefaultMaxLines, true)]     // exactly at the cap: delivered whole
    [InlineData(FileOperationsService.DefaultMaxLines + 1, false)] // one line over: explicit refusal
    public async Task FullReadAtAndOverTheDefaultCap(int lineCount, bool shouldSucceed)
    {
        using var sandbox = new Sandbox();
        var content = Lines(lineCount);
        sandbox.Write("file.txt", content);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { path = "file.txt" });
        if (shouldSucceed)
        {
            McpAssert.Success(reply);
            var payload = ServerProcess.Payload(reply);
            Assert.Equal(lineCount, payload.GetProperty("total_lines").GetInt32());
            Assert.Equal(1, payload.GetProperty("start_line").GetInt32());
            Assert.Equal(lineCount, payload.GetProperty("end_line").GetInt32());
            Assert.False(payload.GetProperty("truncated").GetBoolean());
            Assert.False(payload.GetProperty("has_more").GetBoolean());
            Assert.Equal(content, payload.GetProperty("text").GetString());
        }
        else
        {
            McpAssert.ToolError(reply, "resource_limit");
        }
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task RangeWiderThanTheDefaultCapIsRefusedWhileTheExactCapIsDelivered()
    {
        using var sandbox = new Sandbox();
        var lineCount = FileOperationsService.DefaultMaxLines + 1;
        sandbox.Write("file.txt", Lines(lineCount));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);

        // Exactly the cap: delivered, with end_line at EOF.
        var atCap = await server.ToolAsync("read_file", new { path = "file.txt", start_line = 2, end_line = lineCount });
        McpAssert.Success(atCap);
        var payload = ServerProcess.Payload(atCap);
        Assert.Equal(lineCount, payload.GetProperty("total_lines").GetInt32());
        Assert.Equal(2, payload.GetProperty("start_line").GetInt32());
        Assert.Equal(lineCount, payload.GetProperty("end_line").GetInt32());
        Assert.False(payload.GetProperty("truncated").GetBoolean());
        Assert.False(payload.GetProperty("has_more").GetBoolean());
        var text = payload.GetProperty("text").GetString()!;
        Assert.StartsWith("line2\n", text, StringComparison.Ordinal);
        Assert.EndsWith("line" + lineCount + "\n", text, StringComparison.Ordinal);

        // One line wider than the cap: an explicit refusal, not a silent trim.
        McpAssert.ToolError(
            await server.ToolAsync("read_file", new { path = "file.txt", start_line = 1, end_line = lineCount }),
            "resource_limit");
    }

    // ---- max_lines opt-in ---------------------------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task MaxLinesWithoutAllowLargeReadIsInvalidArguments()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "a\nb\nc\nd");
        // The service refuses the combination instead of silently ignoring max_lines.
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new(MaxLines: 2)));

        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        McpAssert.ProtocolError(
            await server.ToolAsync("read_file", new { path = "file.txt", max_lines = 2 }),
            -32602);

        // Control: with the opt-in the same request is a valid capped prefix read.
        var ok = await server.ToolAsync("read_file", new { path = "file.txt", allow_large_read = true, max_lines = 2 });
        McpAssert.Success(ok);
    }

    // ---- invalid ranges ------------------------------------------------------------------------

    [Theory, Trait("Status", "Baseline")]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(3, 2)]
    [InlineData(-5, -1)]
    public async Task InvalidRangesAreRejectedAsArguments(int startLine, int endLine)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "a\nb\nc");
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new(StartLine: startLine, EndLine: endLine)));

        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        McpAssert.ProtocolError(
            await server.ToolAsync("read_file", new { path = "file.txt", start_line = startLine, end_line = endLine }),
            -32602);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task HalfSpecifiedRangeIsRejectedAsArguments()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "a\nb\nc");
        var files = new FileOperationsService(sandbox.Workspace);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => files.ReadFileAsync("file.txt", new(StartLine: 2)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => files.ReadFileAsync("file.txt", new(EndLine: 2)));

        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        McpAssert.ProtocolError(
            await server.ToolAsync("read_file", new { path = "file.txt", start_line = 2 }),
            -32602);
        McpAssert.ProtocolError(
            await server.ToolAsync("read_file", new { path = "file.txt", end_line = 2 }),
            -32602);
    }

    // ---- FS-07 budgets through every read form ---------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task HugeSingleLineIsRefusedByTheLineBudget()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", new string('a', 200) + "\nshort\n");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxLineChars=64"]);
        // Both read forms are refused by the line budget, before the line is materialized.
        McpAssert.ToolError(await server.ToolAsync("read_file", new { path = "file.txt" }), "resource_limit");
        McpAssert.ToolError(
            await server.ToolAsync("read_file", new { path = "file.txt", start_line = 1, end_line = 1 }),
            "resource_limit");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task RangeOverTheResponseBudgetIsRefusedWhileItIsRead()
    {
        using var sandbox = new Sandbox();
        var line = new string('a', 150);
        sandbox.Write("file.txt", line + "\n" + line + "\n");
        var service = new FileOperationsService(sandbox.Workspace)
        {
            Budget = ResourceBudget.Default with { MaxResponseChars = 200 }
        };

        // FS-10/FS-07: the response budget binds every read form, not only the full file;
        // the selected text itself has to fit, and the refusal happens during the read.
        await Assert.ThrowsAsync<ResourceLimitException>(
            () => service.ReadFileAsync("file.txt", new(StartLine: 1, EndLine: 2)));

        // Control: a selection that fits the budget is still delivered in full.
        var fitting = await service.ReadFileAsync("file.txt", new(StartLine: 1, EndLine: 1));
        Assert.Equal(line + "\n", fitting.Text);
    }

    // ---- DTO shape / source generation --------------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public void ReadResultSerializesThroughTheSourceGeneratedContextWithFidelityMetadata()
    {
        // Reflection-based serialization is disabled in the product (AOT), so the read
        // result must round-trip through the source-generated context with a fixed shape.
        var result = new ReadFileResult("file.txt", "a\nb\n", "md5", "sha256", 4, 1, 2, true, true);
        Assert.Equal(
            "{\"path\":\"file.txt\",\"text\":\"a\\nb\\n\",\"md5\":\"md5\",\"sha256\":\"sha256\","
            + "\"total_lines\":4,\"start_line\":1,\"end_line\":2,\"truncated\":true,\"has_more\":true}",
            JsonSerializer.Serialize(result, McpJsonContext.Default.ReadFileResult));

        // An empty selection omits start_line/end_line: null is expressed by absence.
        var empty = new ReadFileResult("file.txt", "", "md5", "sha256", 0, null, null, false, false);
        Assert.Equal(
            "{\"path\":\"file.txt\",\"text\":\"\",\"md5\":\"md5\",\"sha256\":\"sha256\","
            + "\"total_lines\":0,\"truncated\":false,\"has_more\":false}",
            JsonSerializer.Serialize(empty, McpJsonContext.Default.ReadFileResult));
    }

    // ---- the closing LF is charged against the budget while the read is still running ----

    /// <summary>
    /// The body of the selected line fits the response budget exactly; only its closing
    /// LF crosses it. The refusal must fire in the sink — while the stream is still being
    /// read — and stop the scan on the first chunk, not after the multi-megabyte tail: an
    /// over-budget refusal carries neither text nor hash, so there is no tail worth
    /// reading (FS-10 / FS-07 R1, R8). Both read forms are covered: range and the
    /// allow_large_read prefix cap.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task ClosingLfOverTheResponseBudgetStopsTheReadImmediately()
    {
        using var sandbox = new Sandbox();
        const int lineChars = 200;
        const long fileBytes = 4L * 1024 * 1024;
        var pattern = new string('a', lineChars) + "\ntail\n";
        // The budget equals the line body exactly, so the body passes the text check and
        // only the +1 for the closing LF can refuse the read.
        var budget = ResourceBudget.Default with { MaxResponseChars = lineChars };

        // Range form: line 1 is selected, the refusal fires on its closing LF.
        PatternStream? ranged = null;
        var rangeError = await Assert.ThrowsAsync<ResourceLimitException>(() => new FileOperationsService(sandbox.Workspace)
        {
            Budget = budget,
            OpenReadStreamForTests = _ => ranged = new PatternStream(fileBytes, pattern)
        }.ReadFileAsync("file.txt", new ReadFileOptions(StartLine: 1, EndLine: 1)));
        Assert.Equal("resource_limit", rangeError.Code);
        Assert.Contains("maxResponseChars", rangeError.Message);
        Assert.NotNull(ranged);
        Assert.True(ranged!.BytesHandedOut < 64 * 1024,
            $"The reader consumed {ranged.BytesHandedOut} bytes before refusing; the tail must not be read.");
        Assert.True(ranged.BytesHandedOut < fileBytes / 16, "The 4 MiB fixture was read almost whole.");

        // Prefix form (allow_large_read + max_lines): the same refusal on the same line.
        PatternStream? capped = null;
        var capError = await Assert.ThrowsAsync<ResourceLimitException>(() => new FileOperationsService(sandbox.Workspace)
        {
            Budget = budget,
            OpenReadStreamForTests = _ => capped = new PatternStream(fileBytes, pattern)
        }.ReadFileAsync("file.txt", new ReadFileOptions(AllowLargeRead: true, MaxLines: 1)));
        Assert.Equal("resource_limit", capError.Code);
        Assert.Contains("maxResponseChars", capError.Message);
        Assert.NotNull(capped);
        Assert.True(capped!.BytesHandedOut < 64 * 1024,
            $"allow_large_read consumed {capped.BytesHandedOut} bytes before refusing.");
        Assert.True(capped.BytesHandedOut < fileBytes / 16, "The 4 MiB fixture was read almost whole.");

        // Control: one more char of budget admits the closing LF, and the identical
        // selection is delivered with text length exactly lineChars + 1.
        var control = new PatternStream(fileBytes, pattern);
        var fitting = await new FileOperationsService(sandbox.Workspace)
        {
            Budget = budget with { MaxResponseChars = lineChars + 1 },
            OpenReadStreamForTests = _ => control
        }.ReadFileAsync("file.txt", new ReadFileOptions(StartLine: 1, EndLine: 1));
        Assert.Equal(lineChars + 1, fitting.Text.Length);
        Assert.True(fitting.Text.EndsWith('\n'), "The closing LF is part of the answer when it fits.");
    }

    private static string Lines(int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => "line" + i + "\n"));

    /// <summary>
    /// Minimal generated stream for the budget cases: repeats a pattern up to the declared
    /// length and counts the bytes it handed out, so a test can prove the reader stopped on
    /// an early chunk instead of draining the tail. Not seekable, so the byte budget is
    /// only ever checked by counting.
    /// </summary>
    private sealed class PatternStream : Stream
    {
        private readonly byte[] _pattern;
        private readonly long _length;
        private long _position;

        internal PatternStream(long length, string pattern)
        {
            _length = length;
            _pattern = Sandbox.Utf8.GetBytes(pattern);
        }

        internal long BytesHandedOut { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("No length.");
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException("Pattern streams are read-only.");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _length)
            {
                return ValueTask.FromResult(0);
            }

            var count = (int)Math.Min(buffer.Length, _length - _position);
            for (var i = 0; i < count; i++)
            {
                buffer.Span[i] = _pattern[(int)((_position + i) % _pattern.Length)];
            }

            _position += count;
            BytesHandedOut += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
