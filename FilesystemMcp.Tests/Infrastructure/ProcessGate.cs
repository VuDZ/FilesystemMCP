using System.IO.Pipes;

namespace FilesystemMcp.Tests.Infrastructure;

/// <summary>
/// Named-pipe barrier for the protected TestHost. The host reports every configured
/// atomic-write point and waits for a command: <c>continue</c> releases it, anything
/// else makes the host throw an <see cref="IOException"/>. Releasing with a command the
/// host rejects is therefore a deterministic way to produce a genuinely unexpected
/// in-process defect (a harness failure, not a client mistake) without adding any
/// production seam or CLI option.
/// </summary>
internal sealed class ProcessGate : IAsyncDisposable
{
    private const string DefaultPoints = "BeforeLock,LockContended,BeforeCommit";
    private readonly string _name = "filesystemmcp-tests-" + Guid.NewGuid().ToString("N");
    private readonly string _points;
    private readonly NamedPipeServerStream _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    internal ProcessGate(string? points = null)
    {
        _points = points ?? DefaultPoints;
        _pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    internal async Task<ServerProcess> Start(string workspace, string[]? options = null)
    {
        var connected = _pipe.WaitForConnectionAsync();
        var server = await ServerProcess.StartAsync(workspace, options, executable: ServerProcess.ProtectedExecutable,
            environment: new Dictionary<string, string>
            { ["FS_TEST_ATOMIC_PIPE"] = _name, ["FS_TEST_ATOMIC_POINTS"] = _points });
        await connected.WaitAsync(TimeSpan.FromSeconds(5));
        _reader = new StreamReader(_pipe, leaveOpen: true);
        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
        return server;
    }

    internal async Task At(string point) =>
        Assert.Equal(point, await _reader!.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));

    internal Task Release() => _writer!.WriteLineAsync("continue");

    /// <summary>Releases the barrier with a command the host rejects, so the host throws.</summary>
    internal Task Abort() => _writer!.WriteLineAsync("abort");

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        await _pipe.DisposeAsync();
    }
}
