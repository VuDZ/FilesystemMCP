using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-01")]
public sealed class WorkspaceLinksTests
{
    [WindowsFact, Trait("Status", "Baseline")]
    public async Task ExplicitFalseRejectsExternalJunctionWithoutChangingOutsideFile()
    {
        using var sandbox = new Sandbox();
        var outside = Path.Combine(sandbox.Outside, "secret.txt");
        await File.WriteAllTextAsync(outside, "outside secret", Sandbox.Utf8);
        await sandbox.JunctionAsync("link", sandbox.Outside);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=false"]);
        var read = await server.ToolAsync("read_file", new { path = "link/secret.txt" });
        var replace = await server.ToolAsync("replace_in_file", new
        {
            path = "link/secret.txt", target_snippet = "secret", replacement_snippet = "changed",
            original_hash = FileTextHelper.ComputeContentHashes("outside secret").Sha256
        });
        Assert.Equal("outside secret", await File.ReadAllTextAsync(outside));
        McpAssert.ToolError(read, "symlink_not_allowed");
        McpAssert.ToolError(replace, "symlink_not_allowed");
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task ExplicitFalseRejectsInternalJunction()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("real/file.txt", "inside");
        await sandbox.JunctionAsync("link", Path.Combine(sandbox.Workspace, "real"));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=false"]);
        var reply = await server.ToolAsync("read_file", new { path = "link/file.txt" });
        McpAssert.ToolError(reply, "symlink_not_allowed");
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task DefaultTrueAllowsExternalPhysicalTarget()
    {
        using var sandbox = new Sandbox();
        await File.WriteAllTextAsync(Path.Combine(sandbox.Outside, "secret.txt"), "outside", Sandbox.Utf8);
        await sandbox.JunctionAsync("link", sandbox.Outside);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { path = "link/secret.txt" });
        Assert.Equal("outside", ServerProcess.Payload(reply).GetProperty("text").GetString());
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task TrueAllowsInternalPhysicalTarget()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("real/file.txt", "inside");
        await sandbox.JunctionAsync("link", Path.Combine(sandbox.Workspace, "real"));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=true"]);
        Assert.Equal("inside", ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "link/file.txt" })).GetProperty("text").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public void ParentTraversalIsRejected()
    {
        using var sandbox = new Sandbox();
        Assert.Throws<PathPolicyException>(() => new PathPolicy(sandbox.Workspace, new(false)).Resolve("../outside/secret.txt"));
    }
}