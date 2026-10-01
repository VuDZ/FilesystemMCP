using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-08")]
public sealed class InstallerTests
{
    private static Task<ProcessResult> InstallAsync(Sandbox sandbox) => ProcessRunner.PowerShellAsync([
        "-File", Path.Combine(AppContext.BaseDirectory, "assets", "install2opencode.ps1"),
        "-BinaryPath", ServerProcess.DefaultExecutable, "-WorkspacePath", sandbox.Workspace]);

    [Fact, Trait("Status", "KnownDefect")]
    public async Task ExistingModelServersAndUnknownSettingsArePreserved()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("opencode.json", """
            {"model":"existing-model","custom":{"flag":true},"mcp":{"existing-server":{"type":"local","command":["old"]}}}
            """);
        var result = await InstallAsync(sandbox);
        Assert.True(result.ExitCode == 0, result.Stderr);
        var config = ServerProcess.JsonDocumentParse(await File.ReadAllTextAsync(path));
        Assert.True(config.TryGetProperty("model", out var model), "Installer removed model setting.");
        Assert.Equal("existing-model", model.GetString());
        Assert.True(config.GetProperty("custom").GetProperty("flag").GetBoolean());
        Assert.Equal("old", config.GetProperty("mcp").GetProperty("existing-server").GetProperty("command")[0].GetString());
        Assert.True(config.GetProperty("mcp").TryGetProperty("filesystem-mcp", out _));
    }

    [Fact, Trait("Status", "KnownDefect")]
    public async Task InvalidJsonIsNotOverwritten()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("opencode.json", "{ invalid existing config");
        var original = await File.ReadAllBytesAsync(path);
        var result = await InstallAsync(sandbox);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task FreshInstallUsesUnicodeWorkspaceAsSingleArgument()
    {
        using var sandbox = new Sandbox();
        var result = await InstallAsync(sandbox);
        Assert.True(result.ExitCode == 0, result.Stderr);
        var config = ServerProcess.JsonDocumentParse(await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "opencode.json")));
        Assert.Equal(sandbox.Workspace.Replace('\\', '/'), config.GetProperty("mcp").GetProperty("filesystem-mcp").GetProperty("command")[1].GetString());
        Assert.True(File.Exists(Path.Combine(sandbox.Workspace, "AGENTS.md")));
    }
}
