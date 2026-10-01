using System.Text;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-03")]
public sealed class TextEncodingTests
{
    [Fact, Trait("Status", "Baseline")]
    public async Task InvalidUtf8IsRejectedWithoutReplacingCyrillicWithReplacementCharacters()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("cp1251.txt", "");
        byte[] original = [0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2, 0x20, 0x74, 0x6F, 0x6B, 0x65, 0x6E];
        await File.WriteAllBytesAsync(path, original);
        var error = await Assert.ThrowsAsync<MutationException>(() => new FileService(sandbox.Workspace).ReadFileAsync("cp1251.txt", new()));
        Assert.Equal("unsupported_encoding", error.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8bom"), InlineData("utf16le"), InlineData("utf16be"), InlineData("utf32le"), InlineData("utf32be")]
    public async Task PatchPreservesEncodingBomAndCrLf(string encodingName)
    {
        using var sandbox = new Sandbox();
        var encoding = EncodingFor(encodingName);
        var path = sandbox.Write("file.txt", "Привет old\r\nsecond\r\n", encoding);
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("file.txt", new());
        await files.ReplaceInFileAsync("file.txt", "old", "new", read.Sha256);
        var expected = encoding.GetPreamble().Concat(encoding.GetBytes("Привет new\r\nsecond\r\n")).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8", "start"), InlineData("utf8", "end")]
    [InlineData("utf16le", "start"), InlineData("utf16le", "end")]
    [InlineData("utf16be", "start"), InlineData("utf16be", "end")]
    [InlineData("utf32le", "start"), InlineData("utf32le", "end")]
    [InlineData("utf32be", "start"), InlineData("utf32be", "end")]
    public async Task InvalidSequencesAtEitherEndAreRejectedWithoutMutation(string encodingName, string edge)
    {
        using var sandbox = new Sandbox();
        var payload = InvalidPayload(encodingName, edge);
        var path = sandbox.Write("broken.txt", "");
        await File.WriteAllBytesAsync(path, payload);
        var files = new FileService(sandbox.Workspace);
        var readError = await Assert.ThrowsAsync<MutationException>(() => files.ReadFileAsync("broken.txt", new()));
        Assert.Equal("unsupported_encoding", readError.Code);
        await using (var stream = new ShortReadStream(payload, 1))
        {
            var parsed = await TextDocument.ClassifyAsync(stream);
            Assert.Equal(TextClass.UnsupportedEncoding, parsed.Class);
            Assert.Null(parsed.Document);
        }
        var replaceError = await Assert.ThrowsAsync<MutationException>(() => files.ReplaceInFileAsync("broken.txt", "x", "y", "00"));
        Assert.Equal("unsupported_encoding", replaceError.Code);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8bom", 1), InlineData("utf8bom", 2), InlineData("utf8bom", 3)]
    [InlineData("utf16le", 1), InlineData("utf16le", 2), InlineData("utf16le", 3)]
    [InlineData("utf16be", 1), InlineData("utf16be", 2), InlineData("utf16be", 3)]
    [InlineData("utf32le", 1), InlineData("utf32le", 2), InlineData("utf32le", 3)]
    [InlineData("utf32be", 1), InlineData("utf32be", 2), InlineData("utf32be", 3)]
    public async Task BomSplitAcrossChunksDecodesStrictly(string encodingName, int chunkBytes)
    {
        var encoding = EncodingFor(encodingName);
        var payload = encoding.GetPreamble().Concat(encoding.GetBytes("Привет\r\nA")).ToArray();
        await using var stream = new ShortReadStream(payload, chunkBytes);
        var parsed = await TextDocument.ClassifyAsync(stream);
        Assert.Equal((payload.Length + chunkBytes - 1) / chunkBytes + 1, stream.Reads);
        Assert.Equal(TextClass.Text, parsed.Class);
        Assert.NotNull(parsed.Document);
        Assert.Equal("Привет\r\nA", parsed.Document.Text);
        Assert.Equal("Привет\nA", parsed.Document.Canonical);
        Assert.DoesNotContain('\uFEFF', parsed.Document.Text);
        Assert.Equal(TextDocument.Decode(payload).Canonical, parsed.Document.Canonical);
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8"), InlineData("utf8bom"), InlineData("utf16le"), InlineData("utf16be"), InlineData("utf32le"), InlineData("utf32be")]
    public async Task ReadHashMatchesSharedParserForWholeFileAndLineRange(string encodingName)
    {
        using var sandbox = new Sandbox();
        const string stored = "Привет\r\nbeta\r\ngamma";
        const string canonical = "Привет\nbeta\ngamma";
        var path = sandbox.Write("file.txt", stored, EncodingFor(encodingName));
        var files = new FileService(sandbox.Workspace);
        var full = await files.ReadFileAsync("file.txt", new());
        var range = await files.ReadFileAsync("file.txt", new(StartLine: 2, EndLine: 2));
        await using var stream = new ShortReadStream(await File.ReadAllBytesAsync(path), 1);
        var parsed = await TextDocument.ClassifyAsync(stream);
        Assert.Equal(TextClass.Text, parsed.Class);
        Assert.NotNull(parsed.Document);
        Assert.Equal(stored, parsed.Document.Text);
        Assert.Equal(canonical, parsed.Document.Canonical);
        Assert.Equal(canonical, await FileTextHelper.ReadCanonicalContentAsync(path));
        Assert.Equal(stored, await FileTextHelper.ReadRawContentAsync(path));
        var hash = FileTextHelper.ComputeContentHashes(canonical).Sha256;
        Assert.Equal(hash, full.Sha256);
        Assert.Equal(hash, range.Sha256);
        Assert.Equal(hash, FileTextHelper.ComputeContentHashes(parsed.Document.Canonical).Sha256);
        Assert.Equal("beta", range.Text);
        Assert.NotEqual(FileTextHelper.ComputeContentHashes(range.Text).Sha256, range.Sha256);
        Assert.DoesNotContain('\uFEFF', full.Text);
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8"), InlineData("utf8bom"), InlineData("utf16le"), InlineData("utf16be"), InlineData("utf32le"), InlineData("utf32be")]
    public async Task UntouchedMixedEndingsStayByteIdentical(string encodingName)
    {
        using var sandbox = new Sandbox();
        var encoding = EncodingFor(encodingName);
        const string original = "pre\r\nOLD\rother\npost\r\n";
        const string expected = "pre\r\nX\rY\npost\r\n";
        var path = sandbox.Write("file.txt", original, encoding);
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("file.txt", new());
        await files.ReplaceInFileAsync("file.txt", "OLD\nother", "X\nY", read.Sha256);
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)).ToArray(), await File.ReadAllBytesAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task CrOnlyFileUsesCrForInsertedBreaks()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "a\rb\rold\rc");
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("file.txt", new());
        await files.ReplaceInFileAsync("file.txt", "old", "x\ny", read.Sha256);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes("a\rb\rx\ry\rc"), await File.ReadAllBytesAsync(path));
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("a\r\nb\nold", "old", "x\ny", "a\r\nb\nx\r\ny")]
    [InlineData("a\nb\r\nold", "old", "x\ny", "a\nb\r\nx\ny")]
    [InlineData("a\nb\r\nc\r\nold", "old", "x\ny", "a\nb\r\nc\r\nx\r\ny")]
    [InlineData("plain", "plain", "a\nb", "a\nb")]
    [InlineData("a\nb\nOLD\r\nMORE\nz", "OLD\nMORE", "X\nY", "a\nb\nX\r\nY\nz")]
    public async Task InsertedBreaksUseSpanStyleThenPredominantThenFirst(string original, string target, string replacement, string expected)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", original);
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("file.txt", new());
        await files.ReplaceInFileAsync("file.txt", target, replacement, read.Sha256);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes(expected), await File.ReadAllBytesAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EmptyFileDecodesAsUtf8WithoutBom()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("empty.txt", "");
        await File.WriteAllBytesAsync(path, []);
        var read = await new FileService(sandbox.Workspace).ReadFileAsync("empty.txt", new());
        Assert.Equal("", read.Text);
        Assert.Equal(FileTextHelper.ComputeContentHashes("").Sha256, read.Sha256);
        Assert.Empty(await File.ReadAllBytesAsync(path));
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8bom"), InlineData("utf16le"), InlineData("utf16be"), InlineData("utf32le"), InlineData("utf32be")]
    public async Task BomOnlyFileDecodesToEmptyTextAndPreservesBytes(string encodingName)
    {
        using var sandbox = new Sandbox();
        var payload = EncodingFor(encodingName).GetPreamble();
        var path = sandbox.Write("bom.txt", "");
        await File.WriteAllBytesAsync(path, payload);
        var read = await new FileService(sandbox.Workspace).ReadFileAsync("bom.txt", new());
        Assert.Equal("", read.Text);
        Assert.Equal(FileTextHelper.ComputeContentHashes("").Sha256, read.Sha256);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        await using var stream = new ShortReadStream(payload, 1);
        var parsed = await TextDocument.ClassifyAsync(stream);
        Assert.Equal(TextClass.Text, parsed.Class);
        Assert.NotNull(parsed.Document);
        Assert.Equal("", parsed.Document.Canonical);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task TrailingNewlineSurvivesInteriorReplacement()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "keep\r\n");
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("file.txt", new());
        await files.ReplaceInFileAsync("file.txt", "keep", "KEEP", read.Sha256);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes("KEEP\r\n"), await File.ReadAllBytesAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task MultilineReplacementUsesStyleOfReplacedSpan()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "head\r\nold\r\nmid\r\ntail\r\n");
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("file.txt", new());
        await files.ReplaceInFileAsync("file.txt", "old\nmid", "new\nline\nextra", read.Sha256);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes("head\r\nnew\r\nline\r\nextra\r\ntail\r\n"), await File.ReadAllBytesAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task CreateWritesUtf8WithoutBomAndKeepsSuppliedLineEndings()
    {
        using var sandbox = new Sandbox();
        var created = await new MutationService(sandbox.Workspace).CreateFileAsync("created.txt", "one\r\ntwo\r\n");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(sandbox.Workspace, "created.txt"));
        Assert.Equal(new UTF8Encoding(false, true).GetBytes("one\r\ntwo\r\n"), bytes);
        var hash = FileTextHelper.ComputeContentHashes("one\ntwo\n").Sha256;
        Assert.Equal(hash, created.Sha256);
        Assert.Equal(hash, (await new FileService(sandbox.Workspace).ReadFileAsync("created.txt", new())).Sha256);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EncoderFailureOnFormatPreservingReplaceLeavesOriginalBytes()
    {
        using var sandbox = new Sandbox();
        var encoding = EncodingFor("utf16le");
        var path = sandbox.Write("file.txt", "old\r\nkeep\r\n", encoding);
        var original = await File.ReadAllBytesAsync(path);
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("file.txt", new());
        await Assert.ThrowsAsync<EncoderFallbackException>(() => files.ReplaceInFileAsync("file.txt", "old", "\uD800", read.Sha256));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task NulPolicySeparatesUtf8WithoutBomFromBomEncodings()
    {
        using var sandbox = new Sandbox();
        var binary = sandbox.Write("binary.txt", "");
        byte[] original = [0x41, 0x00, 0x42];
        await File.WriteAllBytesAsync(binary, original);
        var files = new FileService(sandbox.Workspace);
        var error = await Assert.ThrowsAsync<MutationException>(() => files.ReadFileAsync("binary.txt", new()));
        Assert.Equal("binary_file", error.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(binary));
        Assert.Equal(TextClass.Binary, TextDocument.Classify(original).Class);

        var textPath = sandbox.Write("utf16.txt", "A\0B", EncodingFor("utf16le"));
        var read = await files.ReadFileAsync("utf16.txt", new());
        Assert.Equal("A\0B", read.Text);
        var withBom = await File.ReadAllBytesAsync(textPath);
        Assert.Contains((byte)0, withBom);
        Assert.Equal(TextClass.Text, TextDocument.Classify(withBom).Class);
    }

    internal static Encoding EncodingFor(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8bom" => new UTF8Encoding(true, true),
        "utf16le" => new UnicodeEncoding(false, true, true),
        "utf16be" => new UnicodeEncoding(true, true, true),
        "utf32le" => new UTF32Encoding(false, true, true),
        "utf32be" => new UTF32Encoding(true, true, true),
        _ => throw new ArgumentException("Unknown fixture encoding.", nameof(name))
    };

    private static byte[] InvalidPayload(string encodingName, string edge)
    {
        if (encodingName == "utf8")
        {
            return edge == "start" ? [0x80, 0x41, 0x42] : [0x41, 0x42, 0xE2, 0x82];
        }

        var preamble = EncodingFor(encodingName).GetPreamble();
        byte[] body = (encodingName, edge) switch
        {
            ("utf16le", "start") => [0x41],
            ("utf16le", "end") => [0x41, 0x00, 0x41],
            ("utf16be", "start") => [0x41],
            ("utf16be", "end") => [0x00, 0x41, 0x41],
            ("utf32le", "start") => [0x00, 0x00, 0x11, 0x00, 0x41, 0x00, 0x00, 0x00],
            ("utf32le", "end") => [0x41, 0x00, 0x00, 0x00, 0x00, 0x00],
            ("utf32be", "start") => [0x00, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41],
            ("utf32be", "end") => [0x00, 0x00, 0x00, 0x41, 0x00, 0x00],
            _ => throw new ArgumentException("Unknown invalid payload.", nameof(encodingName))
        };
        return preamble.Concat(body).ToArray();
    }

    private sealed class ShortReadStream(byte[] data, int maximum) : MemoryStream(data)
    {
        internal int Reads { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (buffer.Length > maximum) buffer = buffer[..maximum];
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
