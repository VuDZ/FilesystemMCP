using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FilesystemMcp.TestHost;

internal static class Program
{
    /// <summary>
    /// Hang watchdog for one barrier command. A test may legally hold the barrier
    /// across a stdio round-trip (response watchdog 20s) and, for the parked
    /// deadline, across the budget plus timer slack. The previous 10s budget
    /// expired under parallel load while that test was still inside its own
    /// watchdog: the host aborted a live invocation and the assertion failed
    /// even though the server had not misbehaved. Isolated runs never waited
    /// that long. This is a hang detector, not a performance budget.
    /// </summary>
    private static readonly TimeSpan BarrierCommandTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Pipe text is raw UTF-8. A BOM on the first line is not part of the protocol.</summary>
    private static readonly Encoding PipeEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
            // Two pipes, one direction each: a pending read on a pipe blocks a write on
            // that same pipe, and the host has to publish the next report while an
            // earlier invocation is already waiting for its command.
            using var toolReports = ConnectBarrierPipe("FS_TEST_BARRIER_PIPE", "-reports");
            using var toolCommands = ConnectBarrierPipe("FS_TEST_BARRIER_PIPE", "-commands");
            if (toolReports is not null && toolCommands is not null)
            {
                ConfigureToolExecutionBarrier(toolReports, toolCommands);
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
    /// may be parked at once. Reports are numbered under the write lock, and one
    /// dedicated reader thread hands out commands in that same order: the Nth command
    /// releases the Nth report. Two parked invocations are legal evidence for a
    /// two-slot budget, and no tool thread reads the pipe itself.
    /// </summary>
    private static void ConfigureToolExecutionBarrier(NamedPipeClientStream reports, NamedPipeClientStream commands)
    {
        var barrier = new ToolBarrier(reports, commands);
        var points = (Environment.GetEnvironmentVariable("FS_TEST_BARRIER_POINTS") ?? "read_file").Split(',');
        global::FilesystemMcp.TestHooks.ToolExecutionBarrier = toolName =>
        {
            if (!points.Contains(toolName)) return;
            barrier.Park(BarrierReport(toolName));
        };
    }

    /// <summary>
    /// Byte side of the tool barrier. One background thread blocks in
    /// <see cref="StreamReader.ReadLine"/>, so a command is observed without a
    /// thread-pool continuation. The previous wait was
    /// <c>ReadLineAsync().WaitAsync().GetResult()</c> on the tool thread: the
    /// continuation needed another pool thread, and a timed-out
    /// <c>WaitAsync</c> left that read running, which then swallowed the
    /// <c>continue</c> the test had already written. Under a parallel suite the
    /// host aborted the invocation; the same test run alone never hit it.
    /// </summary>
    private sealed class ToolBarrier
    {
        private readonly object _writeLock = new();
        private readonly object _inbox = new();
        private readonly StreamWriter _writer;
        private readonly Queue<string> _commands = new();
        private int _nextTicket;
        private int _nextServe;
        private bool _closed;

        public ToolBarrier(NamedPipeClientStream reports, NamedPipeClientStream commands)
        {
            var reader = new StreamReader(commands, PipeEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            _writer = new StreamWriter(reports, PipeEncoding, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            var thread = new Thread(() => Read(reader))
            {
                IsBackground = true,
                Name = "filesystemmcp-tool-barrier"
            };
            thread.Start();
        }

        public void Park(string report)
        {
            int ticket;
            lock (_writeLock)
            {
                _writer.WriteLine(report);
                ticket = _nextTicket++;
            }

            var command = Take(ticket);
            if (!IsContinue(command))
            {
                throw new IOException("Test host tool barrier did not release normally.");
            }
        }

        /// <summary>
        /// The command written for this report. Tickets are served in report order,
        /// so a continue meant for the first parked call cannot release the second.
        /// </summary>
        private string? Take(int ticket)
        {
            var deadline = Environment.TickCount64 + (long)BarrierCommandTimeout.TotalMilliseconds;
            lock (_inbox)
            {
                while (ticket != _nextServe || _commands.Count == 0)
                {
                    if (_closed && _commands.Count == 0)
                    {
                        return null;
                    }

                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        return null;
                    }

                    Monitor.Wait(_inbox, TimeSpan.FromMilliseconds(remaining));
                }

                var command = _commands.Dequeue();
                _nextServe++;
                Monitor.PulseAll(_inbox);
                return command;
            }
        }

        private void Read(StreamReader reader)
        {
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    lock (_inbox)
                    {
                        _commands.Enqueue(line);
                        Monitor.PulseAll(_inbox);
                    }
                }
            }
            catch (Exception)
            {
                // The test disposes the pipe when the case ends. Waiters turn that into
                // the same harness failure as a missing command. An exception here must
                // not tear the process down from a background thread.
            }
            finally
            {
                lock (_inbox)
                {
                    _closed = true;
                    Monitor.PulseAll(_inbox);
                }
            }
        }
    }

    private static NamedPipeClientStream? ConnectBarrierPipe(string variable, string? suffix = null)
    {
        if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } name)
        {
            return null;
        }

        var pipe = new NamedPipeClientStream(".", name + suffix, PipeDirection.InOut);
        try
        {
            // Synchronous connect: an async connect completed through the thread pool
            // has the same false timeout as the old barrier read when the machine is
            // busy starting several TestHost processes at once.
            pipe.Connect((int)BarrierCommandTimeout.TotalMilliseconds);
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
