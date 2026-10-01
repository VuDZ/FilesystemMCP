using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-09")]
public sealed class SearchEncodingTests
{
    [Theory, Trait("Status", "KnownDefect")]
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
        var raw = await new SearchTool(sandbox.Workspace).ExecuteAsync(ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }));
        var payload = ServerProcess.JsonDocumentParse(raw);
        // Support today's array and the FS-04 object envelope; encoding behavior is independent.
        var matches = payload.ValueKind == System.Text.Json.JsonValueKind.Array ? payload : payload.GetProperty("matches");
        var match = Assert.Single(matches.EnumerateArray());
        Assert.Equal("файл.txt", match.GetProperty("path").GetString());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
    }
}
