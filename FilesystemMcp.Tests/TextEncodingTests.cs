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
}
