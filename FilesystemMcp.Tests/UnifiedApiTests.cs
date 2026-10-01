using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-12")]
public sealed class UnifiedApiTests
{
    [Theory, Trait("Status", "KnownDefect")]
    [InlineData("read_file"), InlineData("create_file"), InlineData("replace_in_file"), InlineData("list_directory"), InlineData("search"), InlineData("append_to_file")]
    public async Task CustomRpcMethodsAreRejectedInsteadOfExecutingDivergentOrStubOperations(string method)
    {
        using var sandbox = new Sandbox();
        var original = sandbox.Write("file.txt", "old");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.CallAsync(method, new
        {
            path = method == "list_directory" ? "." : method == "create_file" ? "new.txt" : "file.txt",
            content = "new", regex = "old", file_mask = "*.txt", target_snippet = "old", replacement_snippet = "changed",
            original_hash = FileTextHelper.ComputeContentHashes("old").Sha256
        });
        Assert.True(reply.TryGetProperty("error", out var error), "Custom method must be unsupported: " + reply);
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
        Assert.Equal("old", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "new.txt")));
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task ConflictingPathAliasesAreRejected()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("one.txt", "one");
        sandbox.Write("two.txt", "two");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { path = "one.txt", file_path = "two.txt" });
        McpAssert.ProtocolError(reply, -32602);
    }

    [Fact, Trait("Status", "KnownDefect")]
    public void AgentSampleDoesNotAdvertiseAbsentAppendTool()
    {
        var sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "AGENTS.md.sample"));
        Assert.DoesNotContain("append_to_file", sample);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task StandardCreateDoesNotOverwriteExistingFile()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("existing.txt", "valuable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CreateFileTool(sandbox.Workspace).ExecuteAsync(
            ServerProcess.Arguments(new { path = "existing.txt", content = "replacement" })));
        Assert.Equal("valuable", await File.ReadAllTextAsync(path));
    }
}
