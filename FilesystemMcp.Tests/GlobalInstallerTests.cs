using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

/// <summary>
/// Global OpenCode registration. The stored command is only the binary. OpenCode passes
/// the session directory as the process working directory (<c>cwd</c> is "."), so a project
/// path is not written and the install folder of the binary is not the workspace.
/// </summary>
[Trait("Spec", "FS-08")]
[Trait("Status", "Baseline")]
public sealed class GlobalInstallerTests
{
    private static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "assets", "installglobal.ps1");

    [Fact]
    public async Task FreshInstallStoresOnlyTheBinaryAndTheSessionCwd()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        var result = await InstallAsync(configPath);
        AssertSucceeded(result);

        var server = Server(await ConfigAsync(configPath));
        Assert.Equal("local", server.GetProperty("type").GetString());
        Assert.Equal(".", server.GetProperty("cwd").GetString());
        var command = Command(server);
        Assert.Single(command);
        Assert.Equal(Slash(ServerProcess.DefaultExecutable), command[0], ignoreCase: true);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(configPath)!, "AGENTS.md")));
        Assert.Contains("session directory", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingServersArePreservedAndAProjectPathIsDropped()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, """
            {"model":"existing-model","mcp":{"other":{"type":"local","command":["keep"]},"filesystem-mcp":{"type":"remote","command":["old-binary","D:/OldProject","--logDirectory=C:/logs"],"enabled":false,"cwd":"D:/Pinned"}}}
            """);
        var result = await InstallAsync(configPath);
        AssertSucceeded(result);

        var config = await ConfigAsync(configPath);
        Assert.Equal("existing-model", config.GetProperty("model").GetString());
        Assert.Equal("keep", config.GetProperty("mcp").GetProperty("other").GetProperty("command")[0].GetString());
        var server = Server(config);
        Assert.False(server.GetProperty("enabled").GetBoolean());
        Assert.Equal(".", server.GetProperty("cwd").GetString());
        var command = Command(server);
        Assert.Equal(2, command.Length);
        Assert.Equal(Slash(ServerProcess.DefaultExecutable), command[0], ignoreCase: true);
        Assert.Equal("--logDirectory=C:/logs", command[1]);
        Assert.DoesNotContain(command, argument => argument.Contains("OldProject", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondRunWritesNothing()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        AssertSucceeded(await InstallAsync(configPath));
        var bytes = await File.ReadAllBytesAsync(configPath);
        var second = await InstallAsync(configPath);
        AssertSucceeded(second);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(configPath));
        Assert.DoesNotContain("filesystemmcp-backup-", second.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIfDoesNotCreateTheConfigDirectory()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        var result = await InstallAsync(configPath, "-WhatIf");
        AssertSucceeded(result);
        Assert.False(Directory.Exists(Path.GetDirectoryName(configPath)!));
    }

    [Fact]
    public async Task InvalidJsonIsNotOverwritten()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var original = "{ not json"u8.ToArray();
        await File.WriteAllBytesAsync(configPath, original);
        var result = await InstallAsync(configPath);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(configPath));
    }

    [Fact]
    public async Task V2ConfigKeepsSiblingSettingsAndOmitsAProjectPath()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, """
            {"model":"m","mcp":{"timeout":5,"servers":{"other":{"type":"local","command":["keep"]}}}}
            """);
        var result = await InstallAsync(configPath);
        AssertSucceeded(result);

        var config = await ConfigAsync(configPath);
        Assert.Equal("m", config.GetProperty("model").GetString());
        var mcp = config.GetProperty("mcp");
        Assert.Equal(5, mcp.GetProperty("timeout").GetInt32());
        Assert.Equal("keep", mcp.GetProperty("servers").GetProperty("other").GetProperty("command")[0].GetString());
        var server = mcp.GetProperty("servers").GetProperty("filesystem-mcp");
        Assert.Equal(".", server.GetProperty("cwd").GetString());
        Assert.Single(Command(server));
    }

    [Fact]
    public async Task FreshV2SwitchWritesServersMap()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        AssertSucceeded(await InstallAsync(configPath, "-V2"));
        var server = (await ConfigAsync(configPath)).GetProperty("mcp").GetProperty("servers").GetProperty("filesystem-mcp");
        Assert.Equal(".", server.GetProperty("cwd").GetString());
        Assert.Single(Command(server));
    }

    [Fact]
    public async Task JsoncSiblingIsRefused()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        var directory = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(directory);
        var jsonc = Path.Combine(directory, "opencode.jsonc");
        await File.WriteAllTextAsync(jsonc, "{ /* comment */ }\n");
        var result = await InstallAsync(configPath);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(configPath));
        Assert.Equal("{ /* comment */ }\n", await File.ReadAllTextAsync(jsonc));
    }

    [Fact]
    public async Task StrictModeFlagDoesNotBecomeAWorkspacePath()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        AssertSucceeded(await InstallAsync(configPath, "-AllowSymLinks", "false"));
        var command = Command(Server(await ConfigAsync(configPath)));
        Assert.Equal(2, command.Length);
        Assert.Equal("--allowSymLinks=false", command[1]);
    }

    [PwshFact]
    public async Task PwshFreshInstallStoresOnlyTheBinary()
    {
        using var sandbox = new Sandbox();
        var configPath = ConfigPath(sandbox);
        var result = await RunAsync(PwshFactAttribute.Executable ?? "pwsh", configPath);
        AssertSucceeded(result);
        Assert.Single(Command(Server(await ConfigAsync(configPath))));
    }

    private static string ConfigPath(Sandbox sandbox) =>
        Path.Combine(sandbox.Workspace, "home", ".config", "opencode", "opencode.json");

    private static Task<ProcessResult> InstallAsync(string configPath, params string[] options) =>
        RunAsync(ProcessRunner.PowerShellExecutable, configPath, options);

    private static Task<ProcessResult> RunAsync(string powerShell, string configPath, params string[] options) =>
        ProcessRunner.PowerShellAsync(powerShell, new[]
        {
            "-File", ScriptPath,
            "-BinaryPath", ServerProcess.DefaultExecutable,
            "-ConfigPath", configPath
        }.Concat(options));

    private static async Task<JsonElement> ConfigAsync(string configPath) =>
        ServerProcess.JsonDocumentParse(await File.ReadAllTextAsync(configPath));

    private static JsonElement Server(JsonElement config) =>
        config.GetProperty("mcp").GetProperty("filesystem-mcp");

    private static string[] Command(JsonElement server) =>
        server.GetProperty("command").EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static string Slash(string path) => path.Replace('\\', '/');

    private static void AssertSucceeded(ProcessResult result) =>
        Assert.True(result.ExitCode == 0, "exit " + result.ExitCode + Environment.NewLine + result.Stdout + result.Stderr);
}
