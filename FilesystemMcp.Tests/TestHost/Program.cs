using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FilesystemMcp.TestHost;

internal static class Program
{
    /// <summary>Bounded wait for one barrier command; a watchdog, never a delay.</summary>
    private static readonly TimeSpan BarrierCommandTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> Main(string[] args)
    {
        // Set in the child after CLR startup; a host can reset inherited flags.
        if (OperatingSystem.IsWindows()) SetErrorMode(GetErrorMode() | 0x0002);
        try
        {
            // Infrastructure self-test, independent of any production defect.
            if (args is ["--test-host-crash-probe"])
                throw new InvalidOperationException("Test host crash-capture probe");

            // Fault/barrier configuration belongs to this protected host only.
            // The production entry point never reads these environment variables.
            using var atomicPipe = ConnectBarrierPipe("FS_TEST_ATOMIC_PIPE");
            if (atomicPipe is not null)
            {
                var reader = new StreamReader(atomicPipe, leaveOpen: true);
                var writer = new StreamWriter(atomicPipe, leaveOpen: true) { AutoFlush = true };
                var points = (Environment.GetEnvironmentVariable("FS_TEST_ATOMIC_POINTS") ?? "BeforeLock,BeforeCommit").Split(',');
                global::FilesystemMcp.Program.AtomicWritesForHost = new global::FilesystemMcp.AtomicWriteDependencies
                {
                    Hook = point =>
                    {
                        if (!points.Contains(point.ToString())) return;
                        writer.WriteLine(point.ToString());
                        var command = reader.ReadLineAsync().WaitAsync(BarrierCommandTimeout).GetAwaiter().GetResult();
                        if (command != "continue") throw new IOException("Test host barrier did not release normally.");
                    }
                };
            }

            // FS-07: the same protected-host pattern for the tool-execution seam. The
            // production entry point has no barrier CLI, option or environment variable:
            // only this host can park an invocation after it holds its concurrency slot.
            using var toolPipe = ConnectBarrierPipe("FS_TEST_BARRIER_PIPE");
            if (toolPipe is not null)
            {
                ConfigureToolExecutionBarrier(toolPipe);
            }

            var assembly = Assembly.Load("FilesystemMCP");
            var main = assembly.GetType("FilesystemMcp.Program", throwOnError: true)!
                .GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("FilesystemMcp.Program.Main");
            return await (Task<int>)main.Invoke(null, [args])!;
        }
        catch (Exception exception)
        {
            var failure = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            // Preserve EOF/nonzero exit and the original exception. Do not invent MCP
            // replies or turn failures into success; keep Windows crash UI out of tests.
            try { await Console.Error.WriteLineAsync("TEST HOST: server entry point failed:\n" + failure); }
            catch (IOException) { /* Parent may already have closed its error pipe. */ }
            return 1;
        }
    }

    /// <summary>
    /// Installs the FS-07 tool-execution barrier. The host reports every configured point
    /// (the point name of this seam is the tool name) and blocks for one JSON command:
    /// <c>{"command":"continue"}</c> proceeds, anything else throws. Several invocations
    /// may be parked at once, so reports are written under a lock and a single reader
    /// dispatches the commands in the order the test sent them: two parked invocations
    /// are legal evidence for a two-slot budget, and no two threads ever read the same
    /// pipe reader concurrently.
    /// </summary>
    private static void ConfigureToolExecutionBarrier(NamedPipeClientStream pipe)
    {
        var reader = new StreamReader(pipe, leaveOpen: true);
        var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        var writeLock = new object();
        var commandGate = new SemaphoreSlim(1, 1);
        var points = (Environment.GetEnvironmentVariable("FS_TEST_BARRIER_POINTS") ?? "read_file").Split(',');

        global::FilesystemMcp.TestHooks.ToolExecutionBarrier = toolName =>
        {
            if (!points.Contains(toolName)) return;
            lock (writeLock)
            {
                writer.WriteLine(BarrierReport(toolName));
            }

            commandGate.Wait();
            try
            {
                var command = reader.ReadLineAsync().WaitAsync(BarrierCommandTimeout).GetAwaiter().GetResult();
                if (!IsContinue(command))
                {
                    throw new IOException("Test host tool barrier did not release normally.");
                }
            }
            finally
            {
                commandGate.Release();
            }
        };
    }

    private static NamedPipeClientStream? ConnectBarrierPipe(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } name)
        {
            return null;
        }

        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        try
        {
            pipe.ConnectAsync().WaitAsync(BarrierCommandTimeout).GetAwaiter().GetResult();
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <c>{"point":"&lt;toolName&gt;","tool":"&lt;toolName&gt;"}</c>. Written with
    /// <see cref="Utf8JsonWriter"/> because this host runs with reflection-based
    /// serialization disabled, exactly like the production server.
    /// </summary>
    private static string BarrierReport(string toolName)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("point", toolName);
            writer.WriteString("tool", toolName);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Only the exact <c>{"command":"continue"}</c> message proceeds.</summary>
    private static bool IsContinue(string? line)
    {
        if (line is null) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String
                && command.GetString() == "continue";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetErrorMode();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetErrorMode(uint mode);
}
