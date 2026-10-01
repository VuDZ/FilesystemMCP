using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Status", "Baseline")]
public sealed class ProcessLaunchTests
{
    [Fact]
    public async Task EscapingExceptionIsCapturedWithNonzeroExitAndDiagnostic()
    {
        var result = await ProcessRunner.RunAsync(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [ServerProcess.ProtectedExecutable, "--test-host-crash-probe"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("TEST HOST: server entry point failed:", result.Stderr);
        Assert.Contains("System.InvalidOperationException: Test host crash-capture probe", result.Stderr);
        Assert.Equal("", result.Stdout);
    }
}
