using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FilesystemMcp.Tests.Infrastructure;

internal sealed class ServerProcess : IAsyncDisposable
{
    /// <summary>Watchdog for a response that never arrives; never a synchronization device.</summary>
    /// <summary>
    /// How long a test waits for one frame before declaring the server hung. It is a hang
    /// detector, not a performance budget: the whole suite runs its subprocess tests in
    /// parallel, and at full parallelism this machine has repeatedly taken longer than five
    /// seconds to complete the handshake, which produced failures that said more about load
    /// than about the server. Fifteen seconds keeps the detector meaningful (a real hang is
    /// still caught well inside a test run) without turning scheduler pressure into a red
    /// gate. Any test that needs a tighter or looser bound passes its own watchdog.
    /// </summary>
    public static readonly TimeSpan DefaultResponseWatchdog = TimeSpan.FromSeconds(15);

    public static string DefaultExecutable => Environment.GetEnvironmentVariable("FILESYSTEM_MCP_TEST_SERVER")
        ?? ProtectedExecutable;
    public static string ProtectedExecutable => Path.Combine(AppContext.BaseDirectory, "server", "FilesystemMcp.TestHost.dll");
    public string Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>Exit code captured when the process actually left. Meaningful after dispose.</summary>
    public int ExitCode => _exitCode;

    /// <summary>True when <see cref="DisposeAsync"/> killed the process for overrunning the exit budget.</summary>
    public bool KilledByWatchdog => _killedByWatchdog;

    /// <summary>
    /// Raw stdout frames in arrival order. Response ordering is evidence for the
    /// responsiveness contract (a ping that overtakes a parked tool), and a test can
    /// only assert it on the frames it actually observed.
    /// </summary>
    public IReadOnlyList<string> ReceivedFrames
    {
        get
        {
            lock (_received)
            {
                return _received.ToArray();
            }
        }
    }

    /// <summary>Number of frames observed so far, for <see cref="FramesSince"/>.</summary>
    public int ReceivedFrameCount
    {
        get
        {
            lock (_received)
            {
                return _received.Count;
            }
        }
    }

    private ServerProcess(Process process)
    {
        _process = process;
        _stderrPump = DrainStderrAsync();
    }

    /// <summary>
    /// Frames that arrived after the given position. The handshake frame is part of
    /// <see cref="ReceivedFrames"/>, so a test that asserts on one scenario marks the
    /// position it started from instead of assuming an empty list.
    /// </summary>
    public IReadOnlyList<string> FramesSince(int position)
    {
        lock (_received)
        {
            return _received.Skip(position).ToArray();
        }
    }

    public static async Task<ServerProcess> StartAsync(string workspace, string[]? options = null,
        string? executable = null, bool initialize = true, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        executable ??= DefaultExecutable;
        var isDll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(isDll ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Sandbox.Utf8, StandardOutputEncoding = Sandbox.Utf8, StandardErrorEncoding = Sandbox.Utf8
        };
        if (workingDirectory is not null)
        {
            info.WorkingDirectory = workingDirectory;
        }

        foreach (var pair in environment ?? new Dictionary<string, string>())
        {
            info.Environment[pair.Key] = pair.Value;
        }

        if (isDll)
        {
            info.ArgumentList.Add(executable);
        }

        info.ArgumentList.Add(workspace);
        foreach (var option in options ?? [])
        {
            info.ArgumentList.Add(option);
        }

        var server = new ServerProcess(Process.Start(info) ?? throw new InvalidOperationException("Cannot start MCP test process."));
        try
        {
            if (initialize)
            {
                var reply = await server.CallAsync("initialize", new
                {
                    protocolVersion = "2024-11-05", capabilities = new { },
                    clientInfo = new { name = "FilesystemMcp.Tests", version = "1.0" }
                });
                Assert.True(reply.TryGetProperty("result", out _), "Handshake failed: " + reply + " " + server.Stderr);
                await server.NotifyAsync("notifications/initialized", new { });
            }
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public static JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value);
    public static JsonElement Payload(JsonElement response)
    {
        Assert.False(response.TryGetProperty("error", out _), "Unexpected protocol error: " + response);
        return JsonDocumentParse(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
    }
    public static JsonElement JsonDocumentParse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public Task<JsonElement> ToolAsync(string name, object arguments) => CallAsync("tools/call", new { name, arguments });

    /// <summary>
    /// Sends a tool call and returns its id without reading stdout. Pair with
    /// <see cref="ReadResponseAsync"/> after the last <see cref="ProcessGate"/> release:
    /// the response watchdog starts only then, so time the server spends parked on the
    /// barrier is not charged against the deadline.
    /// </summary>
    public Task<int> SendToolAsync(string name, object arguments) => SendCallAsync("tools/call", new { name, arguments });

    public async Task<int> SendCallAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref _id);
        await SendRawAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        return id;
    }

    public async Task<JsonElement> CallAsync(string method, object parameters) =>
        await ReadResponseAsync(await SendCallAsync(method, parameters));

    public async Task<JsonElement> ReadResponseAsync(int id, TimeSpan? watchdog = null)
    {
        var line = await ReadRawLineAsync(watchdog);
        var response = JsonDocumentParse(line);
        Assert.True(response.TryGetProperty("id", out var actual) && actual.ValueKind == JsonValueKind.Number
            && actual.GetInt32() == id, $"Expected the response for id {id}, received another frame: {line}");
        return response;
    }
    public Task NotifyAsync(string method, object parameters) => SendRawAsync(
        JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }));

    /// <summary>Writes an arbitrary raw frame. Historical name, kept for existing callers.</summary>
    public Task SendRawAsync(string frame) => SendRawLineAsync(frame);

    /// <summary>
    /// Writes an arbitrary frame plus the line terminator; no serialization, so a test
    /// can control the exact frame length (oversized and boundary frames).
    /// </summary>
    public async Task SendRawLineAsync(string frame)
    {
        await _process.StandardInput.WriteLineAsync(frame);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>
    /// Byte-level variant of <see cref="SendRawLineAsync"/>. It exists so a test can put a
    /// frame on the wire that is not valid text at all (invalid UTF-8), which the
    /// encoder of the standard-input writer could never produce. The text writer is
    /// flushed first, so no buffered text can overtake these bytes.
    /// </summary>
    public async Task SendRawBytesAsync(byte[] frame)
    {
        await _process.StandardInput.FlushAsync();
        await _process.StandardInput.BaseStream.WriteAsync(frame);
        await _process.StandardInput.BaseStream.FlushAsync();
    }

    /// <summary>
    /// Reads one raw stdout line. <paramref name="watchdog"/> only fails a hung test;
    /// it is never used to synchronize two operations, and the default keeps the
    /// historical 5 second deadline for every existing caller.
    /// </summary>
    public async Task<string> ReadRawLineAsync(TimeSpan? watchdog = null)
    {
        var budget = watchdog ?? DefaultResponseWatchdog;
        string? line;
        try
        {
            line = await _process.StandardOutput.ReadLineAsync().WaitAsync(budget);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"Server did not return a response within {budget.TotalSeconds:0.###}s. stderr: " + Stderr);
        }

        Assert.True(line is not null, "Server exited before response: " + Stderr);
        lock (_received)
        {
            _received.Add(line!);
        }

        return line!;
    }

    public async Task<JsonElement> ReadAsync(TimeSpan? watchdog = null) =>
        JsonDocumentParse(await ReadRawLineAsync(watchdog));

    /// <summary>Closes stdin so the server observes EOF and shuts down.</summary>
    public void CloseInput() => _process.StandardInput.Close();

    /// <summary>
    /// Waits until the process leaves. A hang is killed and recorded on
    /// <see cref="KilledByWatchdog"/>; this method does not treat the kill as success.
    /// </summary>
    public async Task WaitForShutdownAsync(TimeSpan? watchdog = null)
    {
        var budget = watchdog ?? DefaultResponseWatchdog;
        try
        {
            await _process.WaitForExitAsync().WaitAsync(budget);
        }
        catch (TimeoutException)
        {
            _killedByWatchdog = true;
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        try
        {
            _exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // The process never produced an exit code.
        }
    }

    /// <summary>Stdout bytes not yet consumed by <see cref="ReadRawLineAsync"/>.</summary>
    public Task<string> ReadRemainingStdoutAsync() => _process.StandardOutput.ReadToEndAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    _killedByWatchdog = true;
                    _process.Kill(entireProcessTree: true);
                }
            }
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try
            {
                if (_process.HasExited)
                {
                    _exitCode = _process.ExitCode;
                }
            }
            catch (InvalidOperationException)
            {
                // The process never produced an exit code.
            }

            _process.Dispose();
        }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[2048];
        int count;
        while ((count = await _process.StandardError.ReadAsync(buffer)) > 0)
        {
            lock (_stderr)
            {
                if (_stderr.Length < 131072)
                {
                    _stderr.Append(buffer, 0, Math.Min(count, 131072 - _stderr.Length));
                }
            }
        }
    }

    private readonly Process _process;
    private readonly Task _stderrPump;
    private readonly StringBuilder _stderr = new();
    private readonly List<string> _received = [];
    private int _id;
    private int _exitCode;
    private bool _killedByWatchdog;
}
