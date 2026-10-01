using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-06")]
public sealed class LoggingTests
{
    [Fact, Trait("Status", "KnownDefect")]
    public async Task UnavailableLogDirectoryDoesNotFailCreateOrTerminateServer()
    {
        using var sandbox = new Sandbox();
        // Deliberate entry-point failures always use the protected managed host,
        // even when other tests target an externally published binary.
        var executable = sandbox.CopyServer("blocked-log-server", protectedHost: true);
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        await File.WriteAllTextAsync(logPath, "fixture occupying directory name");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);
        var response = await server.ToolAsync("create_file", new { path = "created.txt", content = "valuable" });
        Assert.False(response.TryGetProperty("error", out _));
        Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("valuable", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "created.txt")));
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task ContentSecretDoesNotAppearInDiagnostics()
    {
        using var sandbox = new Sandbox();
        var executable = sandbox.CopyServer("secret-log-server");
        var logPath = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        const string secret = "TOP_SECRET_FS06_do_not_log";
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--logDirectory=" + logPath], executable);
        await server.ToolAsync("create_file", new { path = "secret.txt", content = secret });
        await server.CallAsync("ping", new { });
        var logs = Directory.Exists(logPath)
            ? string.Join("\n", Directory.EnumerateFiles(logPath).Select(File.ReadAllText)) : "";
        Assert.DoesNotContain(secret, logs + server.Stderr);
    }
}
