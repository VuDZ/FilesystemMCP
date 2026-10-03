using System.Text;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

/// <summary>
/// FS-09 acceptance: search and read_file share one streaming decoder, so every
/// supported BOM encoding is searchable with the same 1-based line numbers, and a file
/// the decoder refuses (binary NUL, invalid sequence) is an explicit FS-04 skip —
/// never a silent «no matches». Every case is deterministic and every fixture lives
/// only inside the temp sandbox.
/// </summary>
[Trait("Spec", "FS-09")]
public sealed class SearchEncodingTests
{
    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf16le"), InlineData("utf16be"), InlineData("utf32le"), InlineData("utf32be")]
    public Task UnicodeBomFilesAreSearchable(string encoding) => CheckEncodingAsync(encoding);

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8"), InlineData("utf8bom")]
    public Task Utf8FilesAreSearchable(string encoding) => CheckEncodingAsync(encoding);

    private static async Task CheckEncodingAsync(string encoding)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("файл.txt", "first\r\nneedle Привет\r\n", TextEncodingTests.EncodingFor(encoding));
        var read = await new FileService(sandbox.Workspace).ReadFileAsync("файл.txt", new());
        Assert.Contains("needle Привет", read.Text);
        var raw = await new SearchTool(sandbox.Workspace).ExecuteAsync(ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default);
        var payload = ServerProcess.JsonDocumentParse(raw);
        var match = Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal("файл.txt", match.GetProperty("path").GetString());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        // A decodable text file is never reported through the skip channel, and the
        // Unicode path stays the logical in-workspace name.
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
        Assert.False(payload.GetProperty("incomplete").GetBoolean());
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8", "first\rneedle Привет\rthird\n")]
    [InlineData("utf8bom", "first\r\nneedle Привет\rthird\n")]
    [InlineData("utf16le", "first\rneedle Привет\rthird\n")]
    [InlineData("utf32be", "first\r\nneedle Привет\rthird\n")]
    public async Task CrOnlyAndMixedEndingsNumberLinesLikeReadFile(string encoding, string stored)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("endings.txt", stored, TextEncodingTests.EncodingFor(encoding));
        // read_file splits on CRLF, LF and lone CR; line 2 must be the very line search
        // reports, so both tools agree on the numbering of the same stored bytes.
        var range = await new FileService(sandbox.Workspace).ReadFileAsync("endings.txt", new(StartLine: 2, EndLine: 2));
        Assert.Equal("needle Привет", range.Text);
        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(sandbox.Workspace).ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        var match = Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8bom"), InlineData("utf16le"), InlineData("utf16be"), InlineData("utf32le"), InlineData("utf32be")]
    public async Task BomOnlyFileIsAnEmptyCompleteResult(string encoding)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("bom.txt", "");
        await File.WriteAllBytesAsync(path, TextEncodingTests.EncodingFor(encoding).GetPreamble());
        Assert.Equal("", (await new FileService(sandbox.Workspace).ReadFileAsync("bom.txt", new())).Text);
        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(sandbox.Workspace).ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        // A BOM-only payload decodes to zero lines: an empty, complete answer — not a
        // skip, and not a truncated one.
        Assert.Equal(0, payload.GetProperty("matches").GetArrayLength());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
        Assert.False(payload.GetProperty("incomplete").GetBoolean());
        Assert.False(payload.GetProperty("truncated").GetBoolean());
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("early"), InlineData("late")]
    public async Task BinaryNulIsAnExplicitSkipEarlyAndLate(string edge)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("clean.txt", "needle");
        var binary = sandbox.Write("binary.txt", "");
        // The late NUL sits well past any 512-byte probe boundary, and "needle" appears
        // before it: the matches collected first must be retracted by the final verdict.
        byte[] stored = edge == "early"
            ? [0x00, .. Encoding.UTF8.GetBytes("needle after the NUL\r\nsecond\r\n")]
            : [.. Encoding.UTF8.GetBytes("needle before the late NUL\r\n" + new string('a', 700) + "\r\n"), 0x00];
        await File.WriteAllBytesAsync(binary, stored);

        var readError = await Assert.ThrowsAsync<MutationException>(() => new FileService(sandbox.Workspace).ReadFileAsync("binary.txt", new()));
        Assert.Equal("binary_file", readError.Code);

        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(sandbox.Workspace).ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        var match = Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal("clean.txt", match.GetProperty("path").GetString());
        var skipped = Assert.Single(payload.GetProperty("skipped").EnumerateArray());
        Assert.Equal("binary_file", skipped.GetProperty("reason").GetString());
        Assert.Equal(skipped.GetProperty("reason").GetString(), skipped.GetProperty("code").GetString());
        Assert.Equal(1, payload.GetProperty("skipped_count").GetInt32());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8"), InlineData("utf16le")]
    public async Task InvalidSequenceAfterTheProbeIsUnsupportedEncoding(string encoding)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("clean.txt", "needle");
        var broken = sandbox.Write("broken.txt", "");
        // The damage sits well past the probe boundary: a decoder that only looked at the
        // head would silently match "needle" in a file that read_file refuses. For UTF-16
        // the trailing odd byte fails only at the decoder's final flush.
        byte[] stored;
        if (encoding == "utf8")
        {
            stored = [.. Encoding.UTF8.GetBytes("needle text\r\n" + new string('a', 600) + "\r\n"), 0xE2, 0x82];
        }
        else
        {
            var encoder = TextEncodingTests.EncodingFor(encoding);
            stored = [.. encoder.GetPreamble(), .. encoder.GetBytes("needle text\r\n" + new string('a', 600) + "\r\n"), 0x41];
        }

        await File.WriteAllBytesAsync(broken, stored);
        var readError = await Assert.ThrowsAsync<MutationException>(() => new FileService(sandbox.Workspace).ReadFileAsync("broken.txt", new()));
        Assert.Equal("unsupported_encoding", readError.Code);

        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(sandbox.Workspace).ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        var match = Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal("clean.txt", match.GetProperty("path").GetString());
        var skipped = Assert.Single(payload.GetProperty("skipped").EnumerateArray());
        Assert.Equal("unsupported_encoding", skipped.GetProperty("reason").GetString());
        Assert.Equal(1, payload.GetProperty("skipped_count").GetInt32());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task BomSplitAcrossShortReadsIsStillDecoded()
    {
        using var sandbox = new Sandbox();
        var encoding = TextEncodingTests.EncodingFor("utf32le");
        var path = sandbox.Write("split.txt", "first\r\nneedle Привет\r\n", encoding);
        var bytes = await File.ReadAllBytesAsync(path);
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            // One byte per read: the signature probe must assemble the four-byte BOM
            // across short reads instead of expecting the head to arrive filled.
            OpenReadStreamForTests = _ => new ShortReadStream(bytes, 1)
        };
        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        var match = Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal("split.txt", match.GetProperty("path").GetString());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task FileThatDisappearsBeforeOpenIsSkippedAsNotFound()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("keep.txt", "needle");
        var victim = sandbox.Write("gone.txt", "needle");
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            BeforeFileSearch = path => { if (PathPolicy.Comparer.Equals(path, victim)) File.Delete(victim); }
        };
        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        var match = Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal("keep.txt", match.GetProperty("path").GetString());
        var skipped = Assert.Single(payload.GetProperty("skipped").EnumerateArray());
        Assert.Equal("file_not_found", skipped.GetProperty("reason").GetString());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task LineOverTheBudgetIsSkippedBeforeItIsMaterialized()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("huge.txt", "needle\r\nsecond line");
        GeneratedLineStream? stream = null;
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace), new ResourceBudget { MaxLineChars = 1024 })
        {
            OpenReadStreamForTests = _ => stream = new GeneratedLineStream(1024L * 1024)
        };
        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        var skipped = Assert.Single(payload.GetProperty("skipped").EnumerateArray());
        Assert.Equal("resource_limit", skipped.GetProperty("reason").GetString());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0, payload.GetProperty("matches").GetArrayLength());
        Assert.NotNull(stream);
        // The FS-07 line budget applies while the line is being assembled: the refusal
        // happens on the first chunk that crosses it, so the rest of the one MiB line is
        // never read, let alone held as a string.
        Assert.True(stream!.BytesHandedOut <= 8 * 1024, $"The search consumed {stream.BytesHandedOut} bytes.");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ReadAndSearchDecodersAgreeOnEveryClassification()
    {
        foreach (var testCase in Cases())
        {
            using var sandbox = new Sandbox();
            var path = sandbox.Write(testCase.Name, "");
            await File.WriteAllBytesAsync(path, testCase.Bytes);

            if (testCase.ReadCode is null)
            {
                await new FileService(sandbox.Workspace).ReadFileAsync(testCase.Name, new());
            }
            else
            {
                var readError = await Assert.ThrowsAsync<MutationException>(
                    () => new FileService(sandbox.Workspace).ReadFileAsync(testCase.Name, new()));
                Assert.Equal(testCase.ReadCode, readError.Code);
            }

            var payload = ServerProcess.JsonDocumentParse(await new SearchTool(sandbox.Workspace).ExecuteAsync(
                ServerProcess.Arguments(new { regex = "needle" }), default));
            if (testCase.SkipCode is null)
            {
                Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
                Assert.Equal(testCase.Matches, payload.GetProperty("matches").GetArrayLength());
                Assert.False(payload.GetProperty("incomplete").GetBoolean());
            }
            else
            {
                var skipped = Assert.Single(payload.GetProperty("skipped").EnumerateArray());
                Assert.Equal(testCase.SkipCode, skipped.GetProperty("reason").GetString());
                Assert.Equal(0, payload.GetProperty("matches").GetArrayLength());
                Assert.True(payload.GetProperty("incomplete").GetBoolean());
            }
        }

        static (string Name, byte[] Bytes, string? ReadCode, string? SkipCode, int Matches)[] Cases()
        {
            var utf16be = TextEncodingTests.EncodingFor("utf16be");
            var utf32le = TextEncodingTests.EncodingFor("utf32le");
            var utf16le = TextEncodingTests.EncodingFor("utf16le");
            return
            [
                ("utf16be.txt", [.. utf16be.GetPreamble(), .. utf16be.GetBytes("needle text\r\n")], null, null, 1),
                ("utf32le.txt", [.. utf32le.GetPreamble(), .. utf32le.GetBytes("first\r\nneedle text\r\n")], null, null, 1),
                ("bom-only.txt", utf16le.GetPreamble(), null, null, 0),
                ("binary.txt", [0x00, .. Encoding.UTF8.GetBytes("needle")], "binary_file", "binary_file", 0),
                ("invalid.txt", [.. Encoding.UTF8.GetBytes("needle"), 0xE2, 0x82], "unsupported_encoding", "unsupported_encoding", 0),
                // UTF-16 without a BOM is not guessed: pure ASCII looks like NUL-padded
                // UTF-8 to the shared decoder, and both tools must say binary_file.
                ("utf16-no-bom.txt", utf16le.GetBytes("needle"), "binary_file", "binary_file", 0),
            ];
        }
    }

    /// <summary>A memory-backed stream that hands out at most <c>maximum</c> bytes per read.</summary>
    private sealed class ShortReadStream(byte[] data, int maximum) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > maximum) buffer = buffer[..maximum];
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>
    /// A non-seekable stream of <c>'a'</c> bytes with no line break: one line of the
    /// requested length, readable only sequentially, counting the bytes it handed out.
    /// </summary>
    private sealed class GeneratedLineStream(long length) : Stream
    {
        private long _remaining = length;

        internal long BytesHandedOut { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("No length.");
        public override long Position { get => 0; set => throw new NotSupportedException("Read-only."); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Fill((byte)'a');
            _remaining -= count;
            BytesHandedOut += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var span = buffer.AsSpan(offset, count);
            var handed = (int)Math.Min(span.Length, _remaining);
            span[..handed].Fill((byte)'a');
            _remaining -= handed;
            BytesHandedOut += handed;
            return handed;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("Read-only.");
        public override void SetLength(long value) => throw new NotSupportedException("Read-only.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read-only.");
    }
}
