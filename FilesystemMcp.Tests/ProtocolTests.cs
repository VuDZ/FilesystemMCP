using System.Text;
using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-13")]
public sealed class ProtocolTests
{
    [Fact, Trait("Status", "Baseline")]
    public async Task MalformedJsonReturnsParseErrorWithExplicitNullId()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        await server.SendRawAsync("{broken");
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":900,\"method\":\"ping\"}");
        var raw = await server.ReadRawLineAsync();
        AssertExplicitNullId(raw);
        var first = ServerProcess.JsonDocumentParse(raw);
        Assert.True(first.TryGetProperty("error", out var error), "Parse error was dropped; received next request instead: " + first);
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
        Assert.False(first.TryGetProperty("result", out _), "parse error must not include result: " + raw);
        Assert.Equal(900, (await server.ReadAsync()).GetProperty("id").GetInt32());
    }

    [Fact, Trait("Status", "Baseline")]
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

    [Theory, Trait("Status", "Baseline")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2099-01-01", "2024-11-05")]
    [InlineData("2025-06-18", "2024-11-05")]
    [InlineData("2024-11-05 ", "2024-11-05")]
    public async Task NegotiatedVersionIsImplementedOrTheBaseline(string requested, string expected)
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, initialize: false);
        var reply = await server.CallAsync("initialize", new
        {
            protocolVersion = requested,
            capabilities = new { experimental = true },
            clientInfo = new { name = "tests", version = "1", title = "ignored" },
            extra = true
        });
        var negotiated = reply.GetProperty("result").GetProperty("protocolVersion").GetString();
        Assert.Equal(expected, negotiated);
        if (!string.Equals(requested, expected, StringComparison.Ordinal))
        {
            Assert.NotEqual(requested, negotiated);
        }
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ParamsArrayIsInvalidParamsInsteadOfParseError()
    {
        // FS-05 owns tools/call params-shape validation, so this case is now a control:
        // a params node of the wrong shape must be -32602, never a syntax parse error.
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.CallAsync("tools/call", new object[] { "invalid params" });
        Assert.Equal(-32602, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task BooleanIdIsInvalidRequest()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"ping\"}");
        var raw = await server.ReadRawLineAsync();
        AssertExplicitNullId(raw);
        var reply = ServerProcess.JsonDocumentParse(raw);
        McpAssert.ProtocolError(reply, -32600);
        Assert.DoesNotContain("\"id\":true", raw, StringComparison.Ordinal);
        await AssertSessionStillAnswersPing(server);
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
        Assert.True((await server.CallAsync("prompts/list", new { })).GetProperty("result").TryGetProperty("prompts", out _));
        Assert.True((await server.CallAsync("resources/list", new { })).GetProperty("result").TryGetProperty("resources", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task UnknownNotificationDoesNotProduceResponse()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        await server.NotifyAsync("notifications/unknown", new { });
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("{broken", -32700, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",}", -32700, "null")]
    [InlineData("null", -32600, "null")]
    [InlineData("true", -32600, "null")]
    [InlineData("42", -32600, "null")]
    [InlineData("\"ping\"", -32600, "null")]
    [InlineData("[]", -32600, "null")]
    [InlineData("[{}]", -32600, "null")]
    [InlineData("{}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\"}", -32600, "null")]
    [InlineData("{\"method\":\"ping\"}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}", -32600, "1")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"ping\"}", -32600, "1")]
    [InlineData("{\"jsonrpc\":2.0,\"id\":1,\"method\":\"ping\"}", -32600, "1")]
    [InlineData("{\"id\":1,\"method\":\"ping\"}", -32600, "1")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":1}", -32600, "1")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"\"}", -32600, "1")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"ping\"}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":false,\"method\":\"ping\"}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"ping\"}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1e2,\"method\":\"ping\"}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{\"a\":1},\"method\":\"ping\"}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":[1],\"method\":\"ping\"}", -32600, "null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":[]}", -32602, "1")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":\"x\"}", -32602, "1")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":1}", -32602, "1")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":true}", -32602, "1")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":null}", -32602, "1")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"no-such-method\"}", -32601, "7")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"no-such-method\",\"params\":[]}", -32601, "8")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"no-such-method\"}", -32600, "null")]
    public async Task InvalidFrameShapesProduceTheJsonRpcCodeForThatShape(string frame, int code, string expectedId)
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        await server.SendRawAsync(frame);
        var raw = await server.ReadRawLineAsync();
        var reply = ServerProcess.JsonDocumentParse(raw);
        AssertError(reply, code, expectedId);
        if (expectedId == "null")
        {
            AssertExplicitNullId(raw);
        }

        await AssertSessionStillAnswersPing(server);
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/unknown\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"no-such-method\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/whatever\",\"params\":[1]}")]
    public async Task ValidNotificationProducesNoResponse(string frame)
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var before = server.ReceivedFrameCount;
        await server.SendRawAsync(frame);
        await AssertSessionStillAnswersPing(server);
        Assert.Equal(before + 1, server.ReceivedFrameCount);
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"ping\"}", "0")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":-7,\"method\":\"ping\"}", "-7")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":-0,\"method\":\"ping\"}", "-0")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"ping\"}", "42")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"ping\"}", "\"abc\"")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"\",\"method\":\"ping\"}", "\"\"")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\"}", "5")]
    public async Task StringAndIntegerIdsAreEchoed(string frame, string expectedId)
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        await server.SendRawAsync(frame);
        var reply = await server.ReadAsync();
        Assert.True(reply.TryGetProperty("result", out _), "expected a ping result: " + reply);
        Assert.False(reply.TryGetProperty("error", out _), reply.ToString());
        Assert.Equal(expectedId, reply.GetProperty("id").GetRawText());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EachRequestGetsExactlyOneResponse()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var before = server.ReceivedFrameCount;
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"ping\"}");
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"ping\"}");
        Assert.Equal(11, (await server.ReadAsync()).GetProperty("id").GetInt32());
        Assert.Equal(12, (await server.ReadAsync()).GetProperty("id").GetInt32());
        Assert.Equal(before + 2, server.ReceivedFrameCount);
    }

    [Theory, Trait("Status", "Baseline")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1,\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":null,\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"   \",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":[],\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":null,\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":\"x\",\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":null}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":[]}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":1,\"version\":\"1\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":null}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":[]}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":null}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}")]
    public async Task InitializeMissingOrMistypedFieldsAreInvalidParamsAndDoNotConsumeTheHandshake(string frame)
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, initialize: false);
        await server.SendRawAsync(frame);
        var reply = await server.ReadAsync();
        AssertError(reply, -32602, "1");
        Assert.NotEqual(-32700, reply.GetProperty("error").GetProperty("code").GetInt32());

        var ok = await server.CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "t", version = "1" }
        });
        Assert.Equal("2024-11-05", ok.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task InitializeWithoutAnIdIsANotificationAndDoesNotStartTheSession()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, initialize: false);
        await server.SendRawAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}");
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}");
        var ping = await server.ReadAsync();
        Assert.Equal(2, ping.GetProperty("id").GetInt32());
        Assert.True(ping.TryGetProperty("result", out _), "initialize without an id produced a response: " + ping);

        var started = await server.CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "t", version = "1" }
        });
        Assert.Equal("2024-11-05", started.GetProperty("result").GetProperty("protocolVersion").GetString());
        var duplicate = await server.CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "t", version = "1" }
        });
        McpAssert.ProtocolError(duplicate, -32600);
        Assert.Contains("already", duplicate.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ToolsBeforeInitializedAreRejectedAndASecondInitializeIsInvalid()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, initialize: false);

        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}");
        var premature = await server.ReadAsync();
        AssertError(premature, -32600, "1");
        Assert.Contains("notifications/initialized", premature.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}");
        Assert.True((await server.ReadAsync()).TryGetProperty("result", out _));

        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"read_file\",\"arguments\":{\"path\":\"x\"}}}");
        var stillClosed = await server.ReadAsync();
        AssertError(stillClosed, -32600, "3");

        var started = await server.CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "t", version = "1" }
        });
        Assert.Equal("2024-11-05", started.GetProperty("result").GetProperty("protocolVersion").GetString());

        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/list\",\"params\":{}}");
        var beforeNotification = await server.ReadAsync();
        AssertError(beforeNotification, -32600, "5");

        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var tools = await server.CallAsync("tools/list", new { });
        Assert.True(tools.GetProperty("result").TryGetProperty("tools", out _));

        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"read_file\",\"params\":{}}");
        var direct = await server.ReadAsync();
        AssertError(direct, -32601, "7");

        var duplicate = await server.CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "t", version = "1" }
        });
        McpAssert.ProtocolError(duplicate, -32600);
        Assert.Contains("already", duplicate.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}");
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task LegacyInitializedNotificationOpensTools()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, initialize: false);
        var started = await server.CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "t", version = "1" }
        });
        Assert.Equal("2024-11-05", started.GetProperty("result").GetProperty("protocolVersion").GetString());
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}");
        Assert.True((await server.CallAsync("tools/list", new { })).GetProperty("result").TryGetProperty("tools", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task BatchIsRejectedWithoutExecutingAnyElement()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var batch = """
            [{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"create_file","arguments":{"path":"created-by-batch.txt","content":"no"}}},{"jsonrpc":"2.0","id":2,"method":"ping"}]
            """;
        await server.SendRawAsync(batch);
        var raw = await server.ReadRawLineAsync();
        AssertExplicitNullId(raw);
        var reply = ServerProcess.JsonDocumentParse(raw);
        AssertError(reply, -32600, "null");
        Assert.Contains("Batch", reply.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "created-by-batch.txt")), "a batch element was executed");
        await AssertSessionStillAnswersPing(server);
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "created-by-batch.txt")));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task InvalidUtf8FrameIsAParseErrorAndTheSessionSurvives()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var frame = Encoding.ASCII.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":903,\"method\":\"ping\",\"params\":{\"x\":\"?\"}}\n");
        var placeholder = Array.IndexOf(frame, (byte)'?');
        Assert.InRange(placeholder, 0, frame.Length - 1);
        frame[placeholder] = 0xFF;
        await server.SendRawBytesAsync(frame);

        var raw = await server.ReadRawLineAsync();
        AssertExplicitNullId(raw);
        var reply = ServerProcess.JsonDocumentParse(raw);
        McpAssert.ProtocolError(reply, -32700);
        Assert.False(reply.TryGetProperty("result", out _), raw);
        await AssertSessionStillAnswersPing(server);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EofShutsTheProcessDownWithCodeZeroAndNoExtraFrame()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var frames = server.ReceivedFrameCount;
        server.CloseInput();
        await server.WaitForShutdownAsync();
        var rest = await server.ReadRemainingStdoutAsync();
        Assert.True(string.IsNullOrEmpty(rest), "EOF wrote a frame: " + rest);
        Assert.Equal(frames, server.ReceivedFrameCount);
        Assert.Equal(0, server.ExitCode);
        Assert.False(server.KilledByWatchdog);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EofMidFrameDiscardsThePartialFrameAndExitsZero()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var frames = server.ReceivedFrameCount;
        await server.SendRawBytesAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"ping\"}"));
        server.CloseInput();
        await server.WaitForShutdownAsync();
        var rest = await server.ReadRemainingStdoutAsync();
        Assert.True(string.IsNullOrEmpty(rest), "a partial frame was answered: " + rest);
        Assert.Equal(frames, server.ReceivedFrameCount);
        Assert.Equal(0, server.ExitCode);
        Assert.False(server.KilledByWatchdog);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task CancellationNotificationProducesNoFrameAndTheRequestGetsOneResponse()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("held.txt", "held");
        await using var gate = new ToolExecutionGate("read_file");
        await using var server = await gate.Start(sandbox.Workspace);
        var before = server.ReceivedFrameCount;

        var id = await server.SendToolAsync("read_file", new { path = "held.txt" });
        Assert.Equal("read_file", await gate.At("read_file"));
        await server.NotifyAsync("notifications/cancelled", new { requestId = id });
        var ping = await server.CallAsync("ping", new { });
        Assert.True(ping.TryGetProperty("result", out _), ping.ToString());
        // The cancel notification is not a frame. The only line since the handshake is the ping.
        Assert.Single(server.FramesSince(before));

        await gate.Release();
        var response = await server.ReadResponseAsync(id, TimeSpan.FromSeconds(20));
        McpAssert.ToolError(response, "cancelled");
        Assert.Equal(id, response.GetProperty("id").GetInt32());
        Assert.Equal(2, server.FramesSince(before).Count);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task OversizedFrameIsAnInvalidRequestWithExplicitNullIdAndTheNextPingWorks()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--maxRequestBytes=1024"]);
        var frame = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\"" + new string('x', 2000) + "\"}}";
        Assert.True(Encoding.UTF8.GetByteCount(frame) > 1024);
        await server.SendRawLineAsync(frame);
        var raw = await server.ReadRawLineAsync();
        AssertExplicitNullId(raw);
        var reply = ServerProcess.JsonDocumentParse(raw);
        AssertError(reply, -32600, "null");
        await AssertSessionStillAnswersPing(server);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task PipelinedInitializedDoesNotOpenAPrematureRequest()
    {
        using var createSandbox = new Sandbox();
        await using (var createServer = await ServerProcess.StartAsync(createSandbox.Workspace, initialize: false))
        {
            var started = await createServer.CallAsync("initialize", new
            {
                protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "t", version = "1" }
            });
            Assert.Equal("2024-11-05", started.GetProperty("result").GetProperty("protocolVersion").GetString());

            // One write: tools/call is already queued when notifications/initialized is read.
            var created = Path.Combine(createSandbox.Workspace, "raced.txt");
            await createServer.SendRawBytesAsync(Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"create_file\",\"arguments\":{\"path\":\"raced.txt\",\"content\":\"no\"}}}\n"
                + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n"));
            var call = await createServer.ReadAsync();
            AssertError(call, -32600, "2");
            Assert.Contains("notifications/initialized", call.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(created), "create_file ran before notifications/initialized");

            var tools = await createServer.CallAsync("tools/list", new { });
            Assert.True(tools.GetProperty("result").TryGetProperty("tools", out _));
            Assert.False(File.Exists(created));
        }

        using var readSandbox = new Sandbox();
        await using var readServer = await ServerProcess.StartAsync(readSandbox.Workspace, initialize: false);
        await readServer.CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "t", version = "1" }
        });
        await readServer.SendRawBytesAsync(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"read_file\",\"params\":{}}\n"
            + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n"));
        var direct = await readServer.ReadAsync();
        AssertError(direct, -32600, "2");
        Assert.Contains("notifications/initialized", direct.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True((await readServer.CallAsync("tools/list", new { })).GetProperty("result").TryGetProperty("tools", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task BlankAndWhitespaceFramesAreParseErrorsWithExplicitNullId()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);

        await server.SendRawBytesAsync(Encoding.UTF8.GetBytes(" \n{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}\n"));
        var spaces = await server.ReadRawLineAsync();
        AssertExplicitNullId(spaces);
        AssertError(ServerProcess.JsonDocumentParse(spaces), -32700, "null");
        Assert.Equal(7, (await server.ReadAsync()).GetProperty("id").GetInt32());

        await server.SendRawBytesAsync(Encoding.UTF8.GetBytes("\n{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"ping\"}\n"));
        var empty = await server.ReadRawLineAsync();
        AssertExplicitNullId(empty);
        AssertError(ServerProcess.JsonDocumentParse(empty), -32700, "null");
        Assert.Equal(8, (await server.ReadAsync()).GetProperty("id").GetInt32());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task EofWhileAToolIsParkedWritesNoFrameAndExitsZero()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("held.txt", "held");
        await using var gate = new ToolExecutionGate("read_file");
        await using var server = await gate.Start(sandbox.Workspace);
        var before = server.ReceivedFrameCount;

        await server.SendToolAsync("read_file", new { path = "held.txt" });
        Assert.Equal("read_file", await gate.At("read_file"));
        server.CloseInput();
        await gate.Release();
        await server.WaitForShutdownAsync();

        var rest = await server.ReadRemainingStdoutAsync();
        Assert.True(string.IsNullOrEmpty(rest), "EOF wrote a frame: " + rest);
        Assert.Empty(server.FramesSince(before));
        Assert.Equal(0, server.ExitCode);
        Assert.False(server.KilledByWatchdog);
        Assert.Equal("held", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "held.txt")));
    }

    private static void AssertError(JsonElement response, int code, string expectedId)
    {
        McpAssert.ProtocolError(response, code);
        Assert.False(response.TryGetProperty("result", out _), "error response must not include result: " + response);
        if (expectedId == "null")
        {
            Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
        }
        else
        {
            Assert.Equal(expectedId, response.GetProperty("id").GetRawText());
        }
    }

    private static void AssertExplicitNullId(string raw) =>
        Assert.True(
            raw.Contains("\"id\":null", StringComparison.Ordinal) || raw.Contains("\"id\": null", StringComparison.Ordinal),
            "Null id was omitted from the frame: " + raw);

    private static async Task AssertSessionStillAnswersPing(ServerProcess server)
    {
        await server.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":9001,\"method\":\"ping\"}");
        var ping = await server.ReadAsync();
        Assert.Equal(9001, ping.GetProperty("id").GetInt32());
        Assert.True(ping.TryGetProperty("result", out _), "the session did not answer the following ping: " + ping);
        Assert.False(ping.TryGetProperty("error", out _), ping.ToString());
    }
}
