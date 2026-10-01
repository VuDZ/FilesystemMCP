using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FilesystemMcp.TestHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Set in the child after CLR startup; a host can reset inherited flags.
        if (OperatingSystem.IsWindows()) SetErrorMode(GetErrorMode() | 0x0002);
        try
        {
            // Infrastructure self-test, independent of any production defect.
            if (args is ["--test-host-crash-probe"])
                throw new InvalidOperationException("Test host crash-capture probe");

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

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetErrorMode();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetErrorMode(uint mode);
}
