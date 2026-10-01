using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-13")]
public sealed class ProtocolTests
{
    [Fact, Trait("Status", "KnownDefect")]
    public async Task MalformedJsonReturnsParseErrorWithExplicitNullId()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        await server.SendRawAsync("{broken");
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":900,\"method\":\"ping\"}");
        var first = await server.ReadAsync();
        Assert.True(first.TryGetProperty("error", out var error), "Parse error was dropped; received next request instead: " + first);
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("id").ValueKind);
        Assert.Equal(900, (await server.ReadAsync()).GetProperty("id").GetInt32());
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task UnsupportedVersionFallsBackToImplementedVersion()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, initialize: false);
        var reply = await server.CallAsync("initialize", new
        {
            protocolVersion = "2099-01-01", capabilities = new { }, clientInfo = new { name = "tests", version = "1" }
        });
        Assert.Equal("2024-11-05", reply.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task ParamsArrayIsInvalidParamsInsteadOfParseError()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.CallAsync("tools/call", new object[] { "invalid params" });
        Assert.Equal(-32602, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task BooleanIdIsInvalidRequest()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"ping\"}");
        var reply = await server.ReadAsync();
        McpAssert.ProtocolError(reply, -32600);
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("id").ValueKind);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task HandshakeListsExactlyTheFiveImplementedToolsAndPingWorks()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var tools = (await server.CallAsync("tools/list", new { })).GetProperty("result").GetProperty("tools");
        var actual = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order(StringComparer.Ordinal).ToArray();
        string[] expected = ["create_file", "list_directory", "read_file", "replace_in_file", "search"];
        Assert.Equal(expected, actual);
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task UnknownNotificationDoesNotProduceResponse()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        await server.NotifyAsync("notifications/unknown", new { });
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }
}
