using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FilesystemMcp.Tests.Infrastructure;

/// <summary>
/// Named-pipe barrier for the protected TestHost around a tool invocation. The host
/// reports every configured barrier point after the invocation has acquired its FS-07
/// concurrency slot and before the tool body runs, then blocks for a command:
/// <c>continue</c> releases it, anything else makes the host throw an
/// <see cref="IOException"/>. Releasing with a command the host rejects is therefore a
/// deterministic way to produce a genuinely unexpected in-process defect (a harness
/// failure, not a client mistake) without adding any production seam or CLI option.
/// </summary>
/// <remarks>
/// Wire protocol, one line of JSON per message, on two pipes. Reports travel
/// host to test (<c>{"point":"&lt;name&gt;","tool":"&lt;toolName&gt;"}</c>) and commands
/// travel test to host (<c>{"command":"continue"}</c> or <c>{"command":"abort"}</c>).
/// One pipe cannot do both: a pending read blocks a write on that same handle, so
/// the second parked invocation would be unable to publish its report. For this
/// seam the point name <em>is</em> the tool name, so
/// <c>FS_TEST_BARRIER_POINTS=read_file</c> holds exactly <c>read_file</c> invocations
/// and nothing else. The barrier is the only synchronization device these tests
/// use: a parked invocation is proof that the request reached the tool body while
/// holding its slot, which no elapsed-time check could establish.
/// </remarks>
internal sealed class ToolExecutionGate : IAsyncDisposable
{
    /// <summary>Every registered tool, so a caller that omits the points holds any invocation.</summary>
    private const string DefaultPoints = "read_file,create_file,replace_in_file,search,list_directory";

    /// <summary>
    /// How long the test waits for the host to report a point. Reaching the tool
    /// body means the child process has been scheduled and the request has been
    /// dispatched; under a full parallel run that has taken longer than 10s while
    /// the same case run alone reports immediately. A hang is still failed, just
    /// not on a budget the scheduler can miss.
    /// </summary>
    private static readonly TimeSpan BarrierWatchdog = TimeSpan.FromSeconds(30);

    /// <summary>Same framing as the host: UTF-8 without a BOM, so the first line stays raw JSON.</summary>
    private static readonly Encoding PipeEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _name = "filesystemmcp-toolgate-" + Guid.NewGuid().ToString("N");
    private readonly string _points;
    private readonly NamedPipeServerStream _reports;
    private readonly NamedPipeServerStream _commands;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private ServerProcess? _server;

    internal ToolExecutionGate(string? points = null)
    {
        _points = points ?? DefaultPoints;
        _reports = new NamedPipeServerStream(_name + "-reports", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _commands = new NamedPipeServerStream(_name + "-commands", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    /// <summary>
    /// Starts the protected host on <paramref name="workspace"/>. <paramref name="points"/>
    /// overrides the points given to the constructor for this server process.
    /// </summary>
    internal async Task<ServerProcess> Start(string workspace, string[]? options = null, string? points = null)
    {
        var reportsConnected = _reports.WaitForConnectionAsync();
        var commandsConnected = _commands.WaitForConnectionAsync();
        var server = await ServerProcess.StartAsync(workspace, options, executable: ServerProcess.ProtectedExecutable,
            environment: new Dictionary<string, string>
            {
                ["FS_TEST_BARRIER_PIPE"] = _name,
                ["FS_TEST_BARRIER_POINTS"] = points ?? _points
            });
        await reportsConnected.WaitAsync(BarrierWatchdog);
        await commandsConnected.WaitAsync(BarrierWatchdog);
        _server = server;
        _reader = new StreamReader(_reports, PipeEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        _writer = new StreamWriter(_commands, PipeEncoding, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        return server;
    }

    /// <summary>
    /// Waits for the host to report <paramref name="point"/> and returns the tool name it
    /// reported. A malformed or unexpected line fails the test instead of being matched
    /// loosely, so a protocol change can never look like a release.
    /// </summary>
    internal async Task<string> At(string point)
    {
        var line = await ReadLineAsync();
        using var document = ParseLine(line);
        var reported = RequireString(document.RootElement, "point", line);
        if (reported != point)
        {
            throw new Xunit.Sdk.XunitException(
                $"The test host reported barrier point '{reported}' while the test expected '{point}'. Line: {line}");
        }

        return RequireString(document.RootElement, "tool", line);
    }

    /// <summary>Releases the parked invocation: the tool body runs next.</summary>
    internal Task Release() => CommandAsync("continue");

    /// <summary>Releases the barrier with a command the host rejects, so the host throws.</summary>
    internal Task Abort() => CommandAsync("abort");

    private Task CommandAsync(string command) =>
        _writer!.WriteLineAsync("{\"command\":" + JsonSerializer.Serialize(command) + "}");

    private async Task<string> ReadLineAsync()
    {
        string? line;
        try { line = await _reader!.ReadLineAsync().WaitAsync(BarrierWatchdog); }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"The test host did not report a barrier point within {BarrierWatchdog.TotalSeconds:0.###}s. stderr: {_server?.Stderr}");
        }

        Assert.True(line is not null, "The test host barrier pipe closed before reporting a point.");
        return line!;
    }

    private static JsonDocument ParseLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"The test host sent a malformed barrier line: {line} ({exception.Message})");
        }
    }

    private static string RequireString(JsonElement element, string property, string line)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString()))
        {
            throw new Xunit.Sdk.XunitException(
                $"The test host sent a barrier line without a string '{property}' property: {line}");
        }

        return value.GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        await _reports.DisposeAsync();
        await _commands.DisposeAsync();
    }
}
