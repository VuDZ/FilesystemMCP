using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-07")]
public sealed class ResourceBudgetsTests
{
    [Fact, Trait("Status", "KnownDefect")]
    public async Task FileByteBudgetAppliesEvenToSmallLineRange()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", new string('a', 200));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxFileBytes=64"]);
        var reply = await server.ToolAsync("read_file", new { path = "file.txt", start_line = 1, end_line = 1 });
        McpAssert.ToolError(reply, "resource_limit");
    }

    [Fact, Trait("Status", "KnownDefect")]
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
}
