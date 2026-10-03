using System.Diagnostics;
using System.Text;

namespace FilesystemMcp.Tests.Infrastructure;

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

internal static class ProcessRunner
{
    /// <summary>
    /// Redirected children write UTF-8 bytes (both Windows PowerShell 5.1 and PowerShell 7 report
    /// <c>utf-8</c> for their standard output here). Without an explicit encoding the parent decodes
    /// them with the console code page, which turns every non-ASCII path in a child's report into
    /// mojibake. The decoder is lenient so a child that emits something else produces a readable
    /// mismatch instead of an exception from the reader.
    /// </summary>
    private static readonly Encoding Utf8Pipe = new UTF8Encoding(false, false);

    public static string PowerShellExecutable => Environment.GetEnvironmentVariable("FILESYSTEM_MCP_TEST_POWERSHELL")
        ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "pwsh");

    public static Task<ProcessResult> PowerShellAsync(IEnumerable<string> arguments) =>
        PowerShellAsync(PowerShellExecutable, arguments);

    /// <summary>
    /// Runs the same command line under an explicitly chosen PowerShell, so a case can be repeated
    /// on PowerShell 7 next to the default Windows PowerShell 5.1 without changing the harness.
    /// </summary>
    public static Task<ProcessResult> PowerShellAsync(string executable, IEnumerable<string> arguments) =>
        RunAsync(executable, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(arguments));

    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Utf8Pipe, StandardErrorEncoding = Utf8Pipe
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start fixture process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    /// <summary>
    /// Runs an entry point whose stdin is already at EOF, so a stdio server performs its
    /// normal shutdown instead of waiting for input. Startup failures and the exit path can
    /// therefore be observed together with the exact stdout/stderr they produced.
    /// </summary>
    internal static async Task<ProcessResult> RunWithClosedInputAsync(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Utf8Pipe, StandardErrorEncoding = Utf8Pipe
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start fixture process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
