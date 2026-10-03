using System.Text;
using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-07")]
public sealed class ResourceBudgetsTests
{
    [Fact, Trait("Status", "Baseline")]
    public async Task FileByteBudgetAppliesEvenToSmallLineRange()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", new string('a', 200));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxFileBytes=64"]);
        var reply = await server.ToolAsync("read_file", new { path = "file.txt", start_line = 1, end_line = 1 });
        McpAssert.ToolError(reply, "resource_limit");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task OversizedFrameIsNotExecutedAndNextPingStillWorks()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxRequestBytes=1024"]);
        await server.SendRawAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 900, method = "tools/call",
            @params = new { name = "create_file", arguments = new { path = "must-not-exist.txt", content = new string('x', 3000) } }
        }));
        var reply = await server.ReadAsync();
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "must-not-exist.txt")), "Oversized request was executed.");
        Assert.Equal(-32600, reply.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("id").ValueKind);
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    // ---- FS-07 file byte budget (stdio) -------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task FileByteBudgetRefusesFullFileRead()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", new string('a', 200));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxFileBytes=64"]);
        McpAssert.ToolError(await server.ToolAsync("read_file", new { path = "file.txt" }), "resource_limit");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task FileByteBudgetIsNotBypassedByAMiddleLineRange()
    {
        // The hash always covers the whole file, so a one-line range read of a file over
        // the byte cap must be refused even though the requested line alone is tiny. No
        // search tool is involved: this is read_file's own budget.
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "alpha\nbravo\ncharlie\n");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxFileBytes=12"]);
        McpAssert.ToolError(
            await server.ToolAsync("read_file", new { path = "file.txt", start_line = 2, end_line = 2 }),
            "resource_limit");

        // Control: the identical request succeeds under the default budget, so the byte
        // cap is the only explanation for the refusal above. FS-10: the delimiter that
        // closed line 2 is part of the returned text.
        await using var control = await ServerProcess.StartAsync(sandbox.Workspace);
        var ok = await control.ToolAsync("read_file", new { path = "file.txt", start_line = 2, end_line = 2 });
        McpAssert.Success(ok);
        Assert.Equal("bravo\n", ServerProcess.Payload(ok).GetProperty("text").GetString());
    }

    // ---- FS-07 line budget (stdio) ------------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task LineCharBudgetRefusesOneLongLineThroughStdio()
    {
        using var sandbox = new Sandbox();
        // One 4096-byte line: far below the 64 MiB default byte budget, so only the line
        // budget can explain the refusal.
        sandbox.Write("file.txt", new string('a', 4096));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxLineChars=64"]);
        McpAssert.ToolError(await server.ToolAsync("read_file", new { path = "file.txt" }), "resource_limit");
        McpAssert.ToolError(
            await server.ToolAsync("read_file", new { path = "file.txt", start_line = 1, end_line = 1 }),
            "resource_limit");

        // Control: the same file under a budget that fits the line is returned in full, so
        // the refusal is the line cap and not a decoder or encoding problem.
        await using var control = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxLineChars=4096"]);
        var ok = await control.ToolAsync("read_file", new { path = "file.txt" });
        McpAssert.Success(ok);
        Assert.Equal(4096, ServerProcess.Payload(ok).GetProperty("text").GetString()!.Length);
    }

    // ---- FS-07 streaming parity with the accepted reference -----------------------------

    [Theory, Trait("Status", "Baseline")]
    [InlineData("", null, null)]
    [InlineData("a", null, null)]
    [InlineData("a\n", null, null)]
    [InlineData("a\nb", null, null)]
    [InlineData("a\r\nb\r\n", null, null)]
    [InlineData("a\rb", null, null)]
    [InlineData("a\r", null, null)]
    [InlineData("\r", null, null)]
    [InlineData("\r\n", null, null)]
    [InlineData("a\r\nb\nc\rd", null, null)]
    [InlineData("a\n\n", null, null)]
    [InlineData("\na\nb", null, null)]
    [InlineData("", 1, 1)]
    [InlineData("a", 1, 1)]
    [InlineData("a\nb\nc\nd", 1, 1)]
    [InlineData("a\nb\nc\nd", 2, 3)]
    [InlineData("a\nb\nc\nd", 4, 4)]
    [InlineData("a\nb\nc\nd", 5, 9)]
    [InlineData("a\r\nb\r\n", 2, 2)]
    [InlineData("\na\nb", 1, 2)]
    public async Task StreamingReaderMatchesTheAcceptedReference(string content, int? startLine, int? endLine)
    {
        using var sandbox = new Sandbox();
        var bytes = Sandbox.Utf8.GetBytes(content);
        var canonical = FileTextHelper.NormalizeLineEndings(content);
        var isFullFileRead = startLine is null && endLine is null;
        var (md5, sha256) = FileTextHelper.ComputeContentHashes(canonical);
        var (expectedText, expectedLines) = FileTextHelper.ExtractRequestedContent(
            canonical, startLine, endLine, FileService.AbsoluteMaxLines, isFullFileRead);

        // One byte per read: the signature, the line breaks and a CRLF pair are forced
        // across chunk boundaries.
        var read = await new FileService(sandbox.Workspace)
        {
            OpenReadStreamForTests = _ => new GeneratedStream(bytes, seekable: true, chunkLimit: 1)
        }.ReadFileAsync("file.txt", new(StartLine: startLine, EndLine: endLine));

        Assert.Equal(expectedText, read.Text);
        Assert.Equal(md5, read.Md5);
        Assert.Equal(sha256, read.Sha256);

        var scanned = await FileContentReader.ReadCanonicalAsync(
            Path.Combine(sandbox.Workspace, "file.txt"),
            startLine,
            endLine,
            isFullFileRead,
            ResourceBudget.Default,
            default,
            _ => new GeneratedStream(bytes, seekable: true, chunkLimit: 1),
            maxLines: isFullFileRead ? FileService.AbsoluteMaxLines : null);

        Assert.Equal(expectedText, scanned.Text);
        Assert.Equal(expectedLines, scanned.TotalLines);
        Assert.Equal(md5, scanned.Md5);
        Assert.Equal(sha256, scanned.Sha256);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task PrefixCapTruncatesLikeTheAcceptedReference()
    {
        using var sandbox = new Sandbox();
        const string content = "a\nb\nc\nd";
        var bytes = Sandbox.Utf8.GetBytes(content);
        var (expectedText, expectedLines) = FileTextHelper.ExtractRequestedContent(
            content, null, null, effectiveMaxLines: 2, isFullFileRead: true);
        // FS-10: the delimiter that closed the capped last line belongs to the answer.
        Assert.Equal("a\nb\n", expectedText);
        Assert.Equal(4, expectedLines);

        var read = await new FileService(sandbox.Workspace)
        {
            OpenReadStreamForTests = _ => new GeneratedStream(bytes, seekable: true, chunkLimit: 2)
        }.ReadFileAsync("file.txt", new(AllowLargeRead: true, MaxLines: 2));

        Assert.Equal(expectedText, read.Text);
        // The hashes still describe the whole file even though the text is a prefix.
        Assert.Equal(FileTextHelper.ComputeContentHashes(content).Sha256, read.Sha256);
    }

    // ---- FS-07 encodings ----------------------------------------------------------------

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8"), InlineData("utf8bom"), InlineData("utf16le"), InlineData("utf16be"), InlineData("utf32le"), InlineData("utf32be")]
    public async Task EverySupportedEncodingMatchesTheSharedParser(string encodingName)
    {
        using var sandbox = new Sandbox();
        const string stored = "Привет\r\nbeta\r\ngamma";
        const string canonical = "Привет\nbeta\ngamma";
        var path = sandbox.Write("file.txt", stored, EncodingFor(encodingName));
        var bytes = await File.ReadAllBytesAsync(path);

        // Reference: the accepted TextDocument/FileTextHelper pipeline.
        Assert.Equal(canonical, await FileTextHelper.ReadCanonicalContentAsync(path));
        var (md5, sha256) = FileTextHelper.ComputeContentHashes(canonical);
        var (_, expectedLines) = FileTextHelper.ExtractRequestedContent(
            canonical, null, null, FileService.AbsoluteMaxLines, isFullFileRead: true);

        // 1 and 3 byte reads split every BOM; 4096 is the production chunk.
        foreach (var chunk in new[] { 1, 3, 4096 })
        {
            var full = await FileContentReader.ReadCanonicalAsync(
                path, null, null, true, ResourceBudget.Default, default,
                _ => new GeneratedStream(bytes, seekable: true, chunkLimit: chunk),
                maxLines: FileService.AbsoluteMaxLines);
            Assert.Equal(canonical, full.Text);
            Assert.Equal(md5, full.Md5);
            Assert.Equal(sha256, full.Sha256);
            Assert.Equal(expectedLines, full.TotalLines);

            var range = await FileContentReader.ReadCanonicalAsync(
                path, 2, 2, false, ResourceBudget.Default, default,
                _ => new GeneratedStream(bytes, seekable: true, chunkLimit: chunk));
            // FS-10: the selected line keeps the delimiter that closed it in the source.
            Assert.Equal("beta\n", range.Text);
            Assert.Equal(md5, range.Md5);
            Assert.Equal(sha256, range.Sha256);
            Assert.Equal(expectedLines, range.TotalLines);
        }

        // The product entry point reads the same bytes through the seam.
        var read = await new FileService(sandbox.Workspace)
        {
            OpenReadStreamForTests = _ => new GeneratedStream(bytes, seekable: true, chunkLimit: 1)
        }.ReadFileAsync("file.txt", new(StartLine: 2, EndLine: 3));
        Assert.Equal("beta\ngamma", read.Text);
        Assert.Equal(sha256, read.Sha256);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task Utf8BomSignatureIsStrippedWhileADecodedBomCharacterStaysText()
    {
        byte[] payload = [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 0x61];
        var reference = TextDocument.Decode(payload);
        Assert.Equal("\uFEFFa", reference.Canonical);
        var (md5, sha256) = FileTextHelper.ComputeContentHashes(reference.Canonical);

        var read = await FileContentReader.ReadCanonicalAsync(
            "bom.txt", null, null, true, ResourceBudget.Default, default,
            _ => new GeneratedStream(payload, seekable: true, chunkLimit: 1));
        Assert.Equal(reference.Canonical, read.Text);
        Assert.Equal(md5, read.Md5);
        Assert.Equal(sha256, read.Sha256);

        var range = await FileContentReader.ReadCanonicalAsync(
            "bom.txt", 1, 1, false, ResourceBudget.Default, default,
            _ => new GeneratedStream(payload, seekable: true, chunkLimit: 2));
        Assert.Equal(reference.Canonical, range.Text);
        Assert.Equal(1, range.TotalLines);
    }

    // ---- FS-07 strictness ---------------------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task InvalidSequenceIsUnsupportedEncoding()
    {
        using var sandbox = new Sandbox();
        byte[] payload = [0x41, 0x42, 0xE2, 0x82];
        var path = sandbox.Write("broken.txt", "");
        await File.WriteAllBytesAsync(path, payload);

        var fileError = await Assert.ThrowsAsync<MutationException>(
            () => new FileService(sandbox.Workspace).ReadFileAsync("broken.txt", new()));
        Assert.Equal("unsupported_encoding", fileError.Code);

        // The incomplete trailing sequence must fail at the final decoder flush, not be
        // dropped or replaced with U+FFFD.
        var streamError = await Assert.ThrowsAsync<MutationException>(() => FileContentReader.ReadCanonicalAsync(
            path, null, null, true, ResourceBudget.Default, default,
            _ => new GeneratedStream(payload, seekable: true, chunkLimit: 2)));
        Assert.Equal("unsupported_encoding", streamError.Code);

        var text = await FileContentReader.ReadCanonicalAsync(
            path, null, null, true, ResourceBudget.Default, default,
            _ => new GeneratedStream(new byte[] { 0x41, 0x42 }, seekable: true, chunkLimit: 2));
        Assert.Equal("AB", text.Text);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task DecodedNulIsBinaryOnlyForUtf8WithoutBom()
    {
        using var sandbox = new Sandbox();
        byte[] original = [0x41, 0x00, 0x42];
        var binaryPath = sandbox.Write("binary.txt", "");
        await File.WriteAllBytesAsync(binaryPath, original);
        var fileError = await Assert.ThrowsAsync<MutationException>(
            () => new FileService(sandbox.Workspace).ReadFileAsync("binary.txt", new()));
        Assert.Equal("binary_file", fileError.Code);
        var streamError = await Assert.ThrowsAsync<MutationException>(() => FileContentReader.ReadCanonicalAsync(
            binaryPath, null, null, true, ResourceBudget.Default, default,
            _ => new GeneratedStream(original, seekable: true, chunkLimit: 1)));
        Assert.Equal("binary_file", streamError.Code);

        // A NUL inside a BOM-prefixed document is text: UTF-16/UTF-32 use NUL bytes for
        // every other byte of an ASCII character.
        var utf16Path = sandbox.Write("utf16.txt", "A\0B", new UnicodeEncoding(false, true, true));
        var utf16 = await new FileService(sandbox.Workspace).ReadFileAsync("utf16.txt", new());
        Assert.Equal("A\0B", utf16.Text);
        Assert.Equal(FileTextHelper.ComputeContentHashes("A\0B").Sha256, utf16.Sha256);

        var utf32Path = sandbox.Write("utf32.txt", "A\0B", new UTF32Encoding(true, true, true));
        Assert.Equal("A\0B", (await new FileService(sandbox.Workspace).ReadFileAsync("utf32.txt", new())).Text);
    }

    // ---- FS-07 bounded streaming proof (generated fixtures) -----------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task ByteBudgetRefusesSyntheticFilesWithoutReadingThemWhole()
    {
        using var sandbox = new Sandbox();
        const long sixtyFourMiB = 64L * 1024 * 1024;
        var budget = ResourceBudget.Default with { MaxFileBytes = 1024 * 1024 };

        // A stream that reports its length is refused before one byte is read.
        var reported = new GeneratedStream(sixtyFourMiB, "a\n", seekable: true);
        var seekableError = await Assert.ThrowsAsync<ResourceLimitException>(() => new FileService(sandbox.Workspace)
        {
            Budget = budget,
            OpenReadStreamForTests = _ => reported
        }.ReadFileAsync("file.txt", new(StartLine: 1, EndLine: 1)));
        Assert.Equal("resource_limit", seekableError.Code);
        Assert.Equal(0, reported.ReadCalls);
        Assert.Equal(0, reported.BytesHandedOut);
        Assert.Equal(0, reported.LargestBufferRequested);

        // A stream without a usable length is counted as it is read and refused as soon as
        // the budget is crossed: only a bounded prefix of the 64 MiB file is handed out,
        // and every buffer the reader asks for stays bounded.
        var counted = new GeneratedStream(sixtyFourMiB, "a\n", seekable: false);
        var countedError = await Assert.ThrowsAsync<ResourceLimitException>(() => new FileService(sandbox.Workspace)
        {
            Budget = budget,
            OpenReadStreamForTests = _ => counted
        }.ReadFileAsync("file.txt", new(StartLine: 1, EndLine: 1)));
        Assert.Equal("resource_limit", countedError.Code);
        Assert.True(counted.BytesHandedOut <= budget.MaxFileBytes + (64 * 1024),
            $"The reader consumed {counted.BytesHandedOut} bytes for a {budget.MaxFileBytes} byte budget.");
        Assert.True(counted.BytesHandedOut < sixtyFourMiB / 4, "The 64 MiB file was read whole.");
        Assert.True(counted.LargestBufferRequested <= 64 * 1024,
            $"The reader requested a {counted.LargestBufferRequested} byte buffer.");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task OneLineFileOverTheLineBudgetIsRefusedWithoutMaterializingIt()
    {
        using var sandbox = new Sandbox();
        GeneratedStream? stream = null;
        var service = new FileService(sandbox.Workspace)
        {
            Budget = ResourceBudget.Default with { MaxLineChars = 1024 },
            OpenReadStreamForTests = _ => stream = new GeneratedStream(1024L * 1024, "a", seekable: false)
        };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = await Assert.ThrowsAsync<ResourceLimitException>(
            () => service.ReadFileAsync("file.txt", new(StartLine: 1, EndLine: 1)));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("resource_limit", error.Code);
        Assert.NotNull(stream);
        // The refusal happens on the first chunk that crosses the line budget: the rest of
        // the one MiB line is never read, let alone held as a string.
        Assert.True(stream!.BytesHandedOut <= 16 * 1024, $"The reader consumed {stream.BytesHandedOut} bytes.");
        Assert.True(stream.LargestBufferRequested <= 64 * 1024,
            $"The reader requested a {stream.LargestBufferRequested} byte buffer.");
        // Supplementary: materializing this one line would cost a 2 MiB UTF-16 string by
        // itself, so an implementation that builds the line before checking the budget
        // cannot stay anywhere near this bound. It is deliberately an order of magnitude
        // above the real cost: this asserts the absence of whole-line materialization,
        // not an exact byte count, so it cannot flake on a GC or a continuation.
        Assert.True(allocated < 1024 * 1024,
            $"FS-07: reading a {stream.BytesHandedOut} byte prefix of a one-line file allocated {allocated} bytes; "
            + "the line must not be materialized before the maxLineChars check.");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task WholeFileHashStreamsForAOneLineRangeOfALargeSyntheticFile()
    {
        using var sandbox = new Sandbox();
        const int lineCount = 400_000;
        const long length = lineCount * 5L;
        var canonical = string.Concat(Enumerable.Repeat("line\n", lineCount));
        Assert.Equal(length, Sandbox.Utf8.GetByteCount(canonical));
        var (md5, sha256) = FileTextHelper.ComputeContentHashes(canonical);

        GeneratedStream? stream = null;
        var read = await new FileService(sandbox.Workspace)
        {
            OpenReadStreamForTests = _ => stream = new GeneratedStream(length, "line\n", seekable: false)
        }.ReadFileAsync("file.txt", new(StartLine: 1, EndLine: 1));

        // FS-10: the selected line keeps the delimiter that closed it in the source.
        Assert.Equal("line\n", read.Text);
        Assert.Equal(md5, read.Md5);
        Assert.Equal(sha256, read.Sha256);
        Assert.NotNull(stream);
        // The whole two megabyte file is streamed for the hash, in bounded chunks only.
        Assert.Equal(length, stream!.BytesHandedOut);
        Assert.True(stream.LargestBufferRequested <= 64 * 1024,
            $"The reader requested a {stream.LargestBufferRequested} byte buffer.");

        var scanned = await FileContentReader.ReadCanonicalAsync(
            "file.txt", 1, 1, false, ResourceBudget.Default, default,
            _ => new GeneratedStream(length, "line\n", seekable: false));
        Assert.Equal("line\n", scanned.Text);
        Assert.Equal(lineCount, scanned.TotalLines);
        Assert.Equal(sha256, scanned.Sha256);

        var allLines = await FileContentReader.ReadAllLinesAsync(
            "file.txt", ResourceBudget.Default, default,
            _ => new GeneratedStream(15, "line\n", seekable: true, chunkLimit: 2));
        Assert.Equal(new[] { "line", "line", "line" }, allLines);
    }

    // ---- FS-07 all-lines reader ---------------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task ReadAllLinesReturnsEveryCanonicalLineAndCapsEachOne()
    {
        // Every line, canonical EOL, no line-count cap; one byte per read splits the CRLF
        // pairs across chunks.
        var lines = await FileContentReader.ReadAllLinesAsync(
            "file.txt", ResourceBudget.Default, default,
            _ => new GeneratedStream(Sandbox.Utf8.GetBytes("a\r\nb\r\n\r\nc"), seekable: true, chunkLimit: 1));
        Assert.Equal(new[] { "a", "b", "", "c" }, lines);

        var oversized = Sandbox.Utf8.GetBytes("ok\ntoolong\n");
        var allLinesError = await Assert.ThrowsAsync<ResourceLimitException>(() => FileContentReader.ReadAllLinesAsync(
            "file.txt", ResourceBudget.Default with { MaxLineChars = 2 }, default,
            _ => new GeneratedStream(oversized, seekable: true, chunkLimit: 1)));
        Assert.Equal("resource_limit", allLinesError.Code);

        // The cap is checked while lines are split, so a line outside the requested range
        // is refused instead of being scanned unbounded.
        var rangeError = await Assert.ThrowsAsync<ResourceLimitException>(() => FileContentReader.ReadCanonicalAsync(
            "file.txt", 1, 1, false, ResourceBudget.Default with { MaxLineChars = 2 }, default,
            _ => new GeneratedStream(oversized, seekable: true, chunkLimit: 1)));
        Assert.Equal("resource_limit", rangeError.Code);
    }

    // ---- FS-07 cancellation -------------------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task PreCancelledTokenRefusesBeforeOpeningTheFile()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "content");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var opened = 0;
        var service = new FileService(sandbox.Workspace)
        {
            OpenReadStreamForTests = _ =>
            {
                opened++;
                return new GeneratedStream(Sandbox.Utf8.GetBytes("content"), seekable: true);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadFileAsync("file.txt", new(), cancelled.Token));
        Assert.Equal(0, opened);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task CancellationFromAStreamCallbackStopsTheRead()
    {
        using var sandbox = new Sandbox();
        var bytes = Sandbox.Utf8.GetBytes("alpha\nbravo\ncharlie\n");

        // The stream hands out its first bytes and cancels the token in the same call, so
        // the cancellation is only observable if the reader checks the token itself.
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileService(sandbox.Workspace)
        {
            OpenReadStreamForTests = _ => new GeneratedStream(bytes, seekable: true, onRead: () => cancellation.Cancel())
        }.ReadFileAsync("file.txt", new(), cancellation.Token));

        // The same callback cancellation is observed by the line-range path.
        using var rangeCancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileContentReader.ReadCanonicalAsync(
            "file.txt", 1, 1, false, ResourceBudget.Default, rangeCancellation.Token,
            _ => new GeneratedStream(bytes, seekable: true, onRead: () => rangeCancellation.Cancel())));

        // ...and by the all-lines reader used by list_directory-style callers.
        using var allLinesCancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileContentReader.ReadAllLinesAsync(
            "file.txt", ResourceBudget.Default, allLinesCancellation.Token,
            _ => new GeneratedStream(bytes, seekable: true, onRead: () => allLinesCancellation.Cancel())));
    }

    // ---- FS-07 materialization bound of a full-file read --------------------------------

    /// <summary>
    /// A full-file read is refused <em>while</em> the text is being materialized, not after
    /// the whole text exists. The fixture is a synthetic 4 MiB stream and the response
    /// budget is 16 KiB, so the read can never be delivered: an implementation without the
    /// bound builds the entire 4 MiB text (8 MiB as UTF-16) and only then lets the transport
    /// replace the answer, which is the materialization FS-07 forbids.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task FullFileReadIsRefusedWhileItIsReadInsteadOfAfterTheWholeTextExists()
    {
        using var sandbox = new Sandbox();
        const int lineChars = 4096;
        const int lineCount = 1024;
        const int responseBudget = 16 * 1024;
        var pattern = new string('a', lineChars) + "\n";
        var fileBytes = (long)pattern.Length * lineCount;
        // maxLineChars is one line exactly, maxFileBytes (64 MiB) is far above 4 MiB, so the
        // response budget is the only bound that can refuse this read. FileService passes it
        // to the reader as textLimit = max(maxLineChars + 1, maxResponseChars) = 16384.
        var budget = ResourceBudget.Default with { MaxLineChars = lineChars, MaxResponseChars = responseBudget };

        GeneratedStream? stream = null;
        var service = new FileService(sandbox.Workspace)
        {
            Budget = budget,
            OpenReadStreamForTests = _ => stream = new GeneratedStream(fileBytes, pattern, seekable: false)
        };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = await Assert.ThrowsAsync<ResourceLimitException>(
            () => service.ReadFileAsync("file.txt", new()));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("resource_limit", error.Code);
        // The refusal names the bound that fired. Without it the read would succeed under
        // this budget and the client would receive the transport's generic replacement
        // instead of a bounded refusal, so the code alone would not catch the regression.
        Assert.Contains("maxResponseChars", error.Message);
        Assert.NotNull(stream);
        // The scan stops on the line that would cross the bound (the fourth line of this
        // fixture), so the stream is never read to the end: reading the file whole would
        // hand out 4 MiB here.
        Assert.True(stream!.BytesHandedOut < 64 * 1024,
            $"The reader consumed {stream.BytesHandedOut} bytes of a {fileBytes} byte file before refusing.");
        Assert.True(stream.BytesHandedOut < fileBytes / 16, "The 4 MiB fixture was read almost whole.");
        Assert.True(stream.LargestBufferRequested <= 64 * 1024,
            $"The reader requested a {stream.LargestBufferRequested} byte buffer.");
        // Supplementary memory proof. Materializing this text costs an 8 MiB UTF-16 string
        // plus a StringBuilder that grows to it by doubling, i.e. at least 16 MiB; the
        // bounded path materializes 16384 characters (32 KiB) and stays around 100 KiB of
        // total allocation. The bound below is an order of magnitude above that real cost,
        // so it asserts the absence of whole-text materialization without being sensitive
        // to a GC or to which thread a continuation resumes on.
        Assert.True(allocated < 1024 * 1024,
            $"Reading a {stream.BytesHandedOut} byte prefix of a {fileBytes} byte file allocated {allocated} bytes; "
            + "the full-file text must not be materialized before the maxResponseChars bound is checked.");
    }

    /// <summary>
    /// The response budget is not raised by <c>allow_large_read</c> (which lifts the
    /// line-count cap instead), and the same fixture is readable: a range inside the bound
    /// is materialized while the whole file is still streamed for the hash, and the full
    /// text is returned once the budget is above it.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task AllowLargeReadDoesNotRaiseTheMaterializationBound()
    {
        using var sandbox = new Sandbox();
        const int lineChars = 4096;
        const int lineCount = 1024;
        var pattern = new string('a', lineChars) + "\n";
        var fileBytes = (long)pattern.Length * lineCount;
        // Canonical text: every line of this fixture is closed by an LF, including the
        // last one, and FS-10 keeps that terminal LF in a full-file read.
        var canonicalChars = lineCount * (lineChars + 1);
        var budget = ResourceBudget.Default with { MaxLineChars = lineChars, MaxResponseChars = 16 * 1024 };

        ReadFileOptions[] raisedLineCap =
        [
            new ReadFileOptions(AllowLargeRead: true),
            new ReadFileOptions(AllowLargeRead: true, MaxLines: 50_000)
        ];
        foreach (var options in raisedLineCap)
        {
            GeneratedStream? large = null;
            var largeError = await Assert.ThrowsAsync<ResourceLimitException>(() => new FileService(sandbox.Workspace)
            {
                Budget = budget,
                OpenReadStreamForTests = _ => large = new GeneratedStream(fileBytes, pattern, seekable: false)
            }.ReadFileAsync("file.txt", options));
            Assert.Equal("resource_limit", largeError.Code);
            Assert.Contains("maxResponseChars", largeError.Message);
            Assert.NotNull(large);
            Assert.True(large!.BytesHandedOut < 64 * 1024,
                $"allow_large_read consumed {large.BytesHandedOut} bytes before refusing the full-file read.");
        }

        // A selected range inside the bound is materialized, while the hashes still cover
        // the whole file: the refusals above are the text bound, not an unreadable fixture.
        GeneratedStream? ranged = null;
        var range = await new FileService(sandbox.Workspace)
        {
            Budget = budget,
            OpenReadStreamForTests = _ => ranged = new GeneratedStream(fileBytes, pattern, seekable: false)
        }.ReadFileAsync("file.txt", new ReadFileOptions(StartLine: 1, EndLine: 2));
        // Two full lines, the LF between them and (FS-10) the LF that closed line 2.
        Assert.Equal((2 * lineChars) + 2, range.Text.Length);
        Assert.Equal(fileBytes, ranged!.BytesHandedOut);

        // Control: the identical full-file read succeeds once the response budget is above
        // the text, so nothing but that budget refused it above.
        var control = new GeneratedStream(fileBytes, pattern, seekable: false);
        var read = await new FileService(sandbox.Workspace)
        {
            Budget = budget with { MaxResponseChars = 16 * 1024 * 1024 },
            OpenReadStreamForTests = _ => control
        }.ReadFileAsync("file.txt", new ReadFileOptions(AllowLargeRead: true));
        Assert.Equal(canonicalChars, read.Text.Length);
        Assert.StartsWith(new string('a', 64), read.Text, StringComparison.Ordinal);
        // FS-10: the terminal LF of the last line is part of the full-file text.
        Assert.EndsWith(new string('a', 64) + "\n", read.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case where the response budget is smaller than one allowed line. The reader splits
    /// lines first and collects the current line in full before comparing it with the text
    /// budget, so <c>maxLineChars</c> — not <c>maxResponseChars</c> — bounds that transient
    /// buffer. FS-07 R0 states that term explicitly; this test pins the behaviour it
    /// describes: the read is refused on the text budget, the refusal names it, and the work
    /// stops within roughly one line rather than scanning the file.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task ResponseBudgetSmallerThanOneLineIsStillTheReportedCause()
    {
        using var sandbox = new Sandbox();
        const int lineChars = 4096;
        const int lineCount = 512;
        var pattern = new string('a', lineChars) + "\n";
        var fileBytes = (long)pattern.Length * lineCount;
        var budget = ResourceBudget.Default with { MaxLineChars = lineChars, MaxResponseChars = 256 };

        GeneratedStream? stream = null;
        var error = await Assert.ThrowsAsync<ResourceLimitException>(() => new FileService(sandbox.Workspace)
        {
            Budget = budget,
            OpenReadStreamForTests = _ => stream = new GeneratedStream(fileBytes, pattern, seekable: false)
        }.ReadFileAsync("file.txt", new ReadFileOptions(AllowLargeRead: true)));

        Assert.Equal("resource_limit", error.Code);
        // The text budget is what refused: the line itself fits maxLineChars and is accepted.
        Assert.Contains("maxResponseChars", error.Message);
        Assert.NotNull(stream);
        // Bounded by roughly one line, not by the file: 512 lines is far more than this.
        Assert.True(stream!.BytesHandedOut <= 2 * lineChars,
            $"The reader consumed {stream.BytesHandedOut} bytes before refusing; the transient line buffer should bound it.");
    }

    // ---- fixtures -----------------------------------------------------------------------

    private static Encoding EncodingFor(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8bom" => new UTF8Encoding(true, true),
        "utf16le" => new UnicodeEncoding(false, true, true),
        "utf16be" => new UnicodeEncoding(true, true, true),
        "utf32le" => new UTF32Encoding(false, true, true),
        "utf32be" => new UTF32Encoding(true, true, true),
        _ => throw new ArgumentException("Unknown fixture encoding.", nameof(name))
    };

    /// <summary>
    /// Test stream that never holds the whole file: it produces the requested bytes from a
    /// repeated pattern, records the largest single buffer it was asked to fill and the
    /// total number of bytes it handed out. <paramref name="onRead"/> runs inside the read
    /// so a test can cancel the token from a stream callback.
    /// </summary>
    private sealed class GeneratedStream : Stream
    {
        private readonly byte[] _pattern;
        private readonly long _length;
        private readonly bool _seekable;
        private readonly int _maxChunk;
        private readonly Action? _onRead;
        private long _position;

        internal GeneratedStream(byte[] content, bool seekable, int? chunkLimit = null, Action? onRead = null)
            : this(content.LongLength, content, seekable, chunkLimit, onRead)
        {
        }

        internal GeneratedStream(long length, string pattern, bool seekable, int? chunkLimit = null, Action? onRead = null)
            : this(length, new UTF8Encoding(false, true).GetBytes(pattern), seekable, chunkLimit, onRead)
        {
        }

        private GeneratedStream(long length, byte[] pattern, bool seekable, int? chunkLimit, Action? onRead)
        {
            // An empty pattern is meaningful only for a zero-length stream: it is how the
            // empty-file case of the parity matrix is expressed, and such a stream is
            // never asked to produce a byte.
            if (pattern.Length == 0 && length != 0)
            {
                throw new ArgumentException("Pattern must not be empty unless the stream is empty.", nameof(pattern));
            }

            _length = length;
            _pattern = pattern;
            _seekable = seekable;
            _maxChunk = chunkLimit ?? int.MaxValue;
            _onRead = onRead;
        }

        internal long BytesHandedOut { get; private set; }
        internal int LargestBufferRequested { get; private set; }
        internal int ReadCalls { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => _seekable;
        public override bool CanWrite => false;
        public override long Length => _seekable ? _length : throw new NotSupportedException("No length.");
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException("Generated streams are read-only.");
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            LargestBufferRequested = Math.Max(LargestBufferRequested, buffer.Length);
            _onRead?.Invoke();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("Generated streams are not seekable.");

        public override void SetLength(long value) => throw new NotSupportedException("Generated streams are read-only.");

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Generated streams are read-only.");

        private int ReadCore(Span<byte> buffer)
        {
            if (_position >= _length)
            {
                return 0;
            }

            var count = (int)Math.Min(Math.Min(buffer.Length, _length - _position), _maxChunk);
            for (var i = 0; i < count; i++)
            {
                buffer[i] = _pattern[(int)((_position + i) % _pattern.Length)];
            }

            _position += count;
            BytesHandedOut += count;
            return count;
        }
    }
}
