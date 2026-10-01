using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-11")]
public sealed class EmptyReplacementTests
{
    [Theory, Trait("Status", "KnownDefect")]
    [InlineData("abc", "b", "", "ac")]
    [InlineData("abc", "abc", "", "")]
    [InlineData("abc", "b", " \n\t", "a \n\tc")]
    [InlineData("a  b", "  ", "_", "a_b")]
    public async Task EmptyAndWhitespaceSnippetsAreValid(string original, string target, string replacement, string expected)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", original);
        var service = new FileService(sandbox.Workspace);
        var read = await service.ReadFileAsync("file.txt", new());
        var raw = await new ReplaceInFileTool(service).ExecuteAsync(ServerProcess.Arguments(new
        {
            path = "file.txt", target_snippet = target, replacement_snippet = replacement, original_hash = read.Sha256
        }));
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes(expected).Sha256, ServerProcess.JsonDocumentParse(raw).GetProperty("new_hash").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EmptyTargetIsRejectedWithoutWrite()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        var service = new FileService(sandbox.Workspace);
        var rawArgs = ServerProcess.Arguments(new { path = "file.txt", target_snippet = "", replacement_snippet = "new", original_hash = FileTextHelper.ComputeContentHashes("original").Sha256 });
        await Assert.ThrowsAsync<ArgumentException>(() => new ReplaceInFileTool(service).ExecuteAsync(rawArgs));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task MissingReplacementIsRejectedWithoutWrite()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        var service = new FileService(sandbox.Workspace);
        var args = ServerProcess.Arguments(new
        {
            path = "file.txt", target_snippet = "original",
            original_hash = FileTextHelper.ComputeContentHashes("original").Sha256
        });
        await Assert.ThrowsAsync<ArgumentException>(() => new ReplaceInFileTool(service).ExecuteAsync(args));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }
}
