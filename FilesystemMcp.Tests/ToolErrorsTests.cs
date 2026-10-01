using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-05")]
public sealed class ToolErrorsTests
{
    [Fact, Trait("Status", "KnownDefect")]
    public async Task MissingFileHasOperationalCode()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { path = "missing.txt" });
        McpAssert.ToolError(reply, "file_not_found");
    }

    [Fact, Trait("Status", "KnownDefect")]
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
        McpAssert.ToolError(reply, "hash_conflict");
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task UnknownToolIsInvalidParams()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        McpAssert.ProtocolError(await server.ToolAsync("not_a_tool", new { }), -32602);
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task MissingRequiredArgumentIsInvalidParams()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        McpAssert.ProtocolError(await server.ToolAsync("read_file", new { }), -32602);
    }
}
