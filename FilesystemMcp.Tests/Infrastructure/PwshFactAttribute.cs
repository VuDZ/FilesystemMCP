namespace FilesystemMcp.Tests.Infrastructure;

/// <summary>
/// Runs one installer case on PowerShell 7 next to the default Windows PowerShell 5.1 lane.
/// The executable is resolved from <c>PATH</c> and probed once per process; when no pwsh is
/// present (or it does not answer with its major version) the case is explicitly skipped
/// instead of failing for an environment reason. <c>FILESYSTEM_MCP_TEST_POWERSHELL</c> is
/// deliberately ignored here: it overrides the *default* shell, which is not necessarily pwsh.
/// </summary>
public sealed class PwshFactAttribute : FactAttribute
{
    private static readonly Lazy<(string? Path, string Reason)> ProbeResult = new(Probe);

    /// <summary>Absolute path to PowerShell 7, or <c>null</c> when the capability probe failed.</summary>
    public static string? Executable => ProbeResult.Value.Path;

    public PwshFactAttribute()
    {
        var (path, reason) = ProbeResult.Value;
        if (path is null)
            Skip = "PowerShell 7 (pwsh) is unavailable: " + reason + ". Run a lane with PowerShell 7 on PATH.";
    }

    private static (string?, string) Probe()
    {
        var executable = FindOnPath();
        if (executable is null) return (null, "no pwsh executable on PATH");
        try
        {
            // Task.Run detaches the probe from any synchronization context that a test runner
            // may have installed, so the blocking wait below cannot deadlock the discovery step.
            var result = Task.Run(() => ProcessRunner.PowerShellAsync(executable, new[]
            {
                "-Command", "[int]$PSVersionTable.PSVersion.Major"
            })).GetAwaiter().GetResult();
            if (result.ExitCode != 0 || !int.TryParse(result.Stdout.Trim(), out var major))
                return (null, $"pwsh did not report its major version (exit {result.ExitCode}: {result.Stderr.Trim()})");
            if (major < 7) return (null, "pwsh reports major version " + major);
            return (executable, string.Empty);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string? FindOnPath()
    {
        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
