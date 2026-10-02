using System.Diagnostics;

namespace FilesystemMcp.Tests.Infrastructure;

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

internal static class ProcessRunner
{
    public static string PowerShellExecutable => Environment.GetEnvironmentVariable("FILESYSTEM_MCP_TEST_POWERSHELL")
        ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "pwsh");

    public static Task<ProcessResult> PowerShellAsync(IEnumerable<string> arguments) =>
        RunAsync(PowerShellExecutable, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(arguments));

    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
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
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
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
