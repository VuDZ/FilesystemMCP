using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

/// <summary>
/// When the workspace argument is omitted, the jail is the working directory the MCP
/// client set at startup. That is the session directory OpenCode passes. An explicit
/// argument still wins, and the directory that contains the executable is not consulted.
/// </summary>
[Trait("Spec", "FS-01")]
[Trait("Status", "Baseline")]
public sealed class ClientDirectoryWorkspaceTests
{
    [Fact]
    public void OmittedWorkspaceUsesTheClientDirectory()
    {
        using var sandbox = new Sandbox();
        var (workspace, options) = ServerOptions.Parse([], sandbox.Workspace);
        Assert.Equal(sandbox.Workspace, workspace);
        Assert.True(options.AllowSymLinks);
    }

    [Fact]
    public void OptionsWithoutAWorkspaceStillUseTheClientDirectory()
    {
        using var sandbox = new Sandbox();
        var (workspace, options) = ServerOptions.Parse(["--allowSymLinks=false"], sandbox.Workspace);
        Assert.Equal(sandbox.Workspace, workspace);
        Assert.False(options.AllowSymLinks);
    }

    [Fact]
    public void ExplicitWorkspaceWinsOverTheClientDirectory()
    {
        using var sandbox = new Sandbox();
        var (workspace, options) = ServerOptions.Parse(
            [sandbox.Workspace, "--allowSymLinks=false"], sandbox.Outside);
        Assert.Equal(sandbox.Workspace, workspace);
        Assert.False(options.AllowSymLinks);
        Assert.Equal(sandbox.Workspace, new PathPolicy(workspace, options).LogicalRoot);
    }

    [Fact]
    public void BlankWorkspaceArgumentIsStillRejected()
    {
        using var sandbox = new Sandbox();
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse([""], sandbox.Workspace));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["  "], sandbox.Workspace));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse([], "  "));
    }

    [Fact]
    public void MissingClientDirectoryFailsWhenThePolicyIsBuilt()
    {
        using var sandbox = new Sandbox();
        var missing = Path.Combine(sandbox.Root, "missing-session");
        var (workspace, options) = ServerOptions.Parse([], missing);
        Assert.Equal(missing, workspace);
        Assert.Throws<DirectoryNotFoundException>(() => new PathPolicy(workspace, options));
    }

    [Fact]
    public async Task ServerJailsToTheDirectoryTheClientSet()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("inside.txt", "inside");
        await File.WriteAllTextAsync(Path.Combine(sandbox.Outside, "secret.txt"), "secret");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, omitWorkspace: true);

        var payload = ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "inside.txt" }));
        Assert.Equal("inside", payload.GetProperty("text").GetString());
        McpAssert.ToolError(
            await server.ToolAsync("read_file", new { path = Path.Combine(sandbox.Outside, "secret.txt") }),
            "path_outside_workspace");
        McpAssert.ToolError(await server.ToolAsync("read_file", new { path = "secret.txt" }), "file_not_found");
    }

    [Fact]
    public async Task ExplicitWorkspaceIgnoresADifferentClientDirectory()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("inside.txt", "inside");
        await File.WriteAllTextAsync(Path.Combine(sandbox.Outside, "only-outside.txt"), "secret");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, workingDirectory: sandbox.Outside);

        var payload = ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "inside.txt" }));
        Assert.Equal("inside", payload.GetProperty("text").GetString());
        McpAssert.ToolError(await server.ToolAsync("read_file", new { path = "only-outside.txt" }), "file_not_found");
    }
}
