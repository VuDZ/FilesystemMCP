using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FilesystemMcp.Tests.Infrastructure;

internal sealed class ServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderrPump;
    private readonly StringBuilder _stderr = new();
    private int _id;
    public static string DefaultExecutable => Environment.GetEnvironmentVariable("FILESYSTEM_MCP_TEST_SERVER")
        ?? ProtectedExecutable;
    public static string ProtectedExecutable => Path.Combine(AppContext.BaseDirectory, "server", "FilesystemMcp.TestHost.dll");
    public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

    private ServerProcess(Process process)
    {
        _process = process;
        _stderrPump = DrainStderrAsync();
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
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var pair in environment ?? new Dictionary<string, string>()) info.Environment[pair.Key] = pair.Value;
        if (isDll) info.ArgumentList.Add(executable);
        info.ArgumentList.Add(workspace);
        foreach (var option in options ?? []) info.ArgumentList.Add(option);
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
        catch { await server.DisposeAsync(); throw; }
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

    public async Task<JsonElement> ReadResponseAsync(int id)
    {
        var response = await ReadAsync();
        Assert.Equal(id, response.GetProperty("id").GetInt32());
        return response;
    }
    public Task NotifyAsync(string method, object parameters) => SendRawAsync(
        JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }));
    public async Task SendRawAsync(string frame)
    {
        await _process.StandardInput.WriteLineAsync(frame);
        await _process.StandardInput.FlushAsync();
    }
    public async Task<JsonElement> ReadAsync()
    {
        string? line;
        try { line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException("Server did not return a response within the watchdog deadline. stderr: " + Stderr);
        }
        Assert.True(line is not null, "Server exited before response: " + Stderr);
        return JsonDocumentParse(line!);
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[2048];
        int count;
        while ((count = await _process.StandardError.ReadAsync(buffer)) > 0)
            lock (_stderr)
                if (_stderr.Length < 131072) _stderr.Append(buffer, 0, Math.Min(count, 131072 - _stderr.Length));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { _process.Kill(entireProcessTree: true); }
            }
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { _process.Dispose(); }
    }
}
