using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-10")]
public sealed class ReadFidelityTests
{
    [Theory, Trait("Status", "KnownDefect")]
    [InlineData("\n\nvalue\n"), InlineData("value\n"), InlineData("\n\n")]
    public async Task FullReadPreservesNormalizedTextIncludingBlankLinesAndTerminalLf(string content)
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", content);
        Assert.Equal(content, (await new FileService(sandbox.Workspace).ReadFileAsync("file.txt", new())).Text);
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task RangeKeepsInitialBlankLine()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "first\n\nthird\nlast");
        var reply = await new FileService(sandbox.Workspace).ReadFileAsync("file.txt", new(StartLine: 2, EndLine: 3));
        Assert.Equal("\nthird\n", reply.Text);
    }

    [Fact, Trait("Status", "KnownDefect")]
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
    public async Task RangeHashDescribesWholeNormalizedFile()
    {
        using var sandbox = new Sandbox();
        const string content = "a\r\nb\r\nc";
        sandbox.Write("file.txt", content);
        var reply = await new FileService(sandbox.Workspace).ReadFileAsync("file.txt", new(StartLine: 2, EndLine: 2));
        Assert.Equal(FileTextHelper.ComputeContentHashes("a\nb\nc").Sha256, reply.Sha256);
    }
}
