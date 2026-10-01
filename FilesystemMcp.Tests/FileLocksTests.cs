using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-04")]
public sealed class FileLocksTests
{
    [WindowsFact, Trait("Status", "KnownDefect")]
    public async Task LockedFileDoesNotDiscardMatchesFromAccessibleFile()
    {
        using var sandbox = new Sandbox();
        var lockedPath = sandbox.Write("locked.txt", "needle");
        sandbox.Write("accessible.txt", "needle");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var response = await server.ToolAsync("search", new { regex = "needle", file_mask = "*.txt" });
        Assert.False(response.TryGetProperty("error", out _), "Search aborted instead of returning partial matches: " + response);
        Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean());
        var payload = ServerProcess.Payload(response);
        Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "accessible.txt");
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.Equal(1, payload.GetProperty("skipped_count").GetInt32());
        Assert.Contains(payload.GetProperty("skipped").EnumerateArray(), item => item.GetProperty("reason").GetString() == "file_locked");
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task ReadIsAllowedWhenOtherHandleSharesRead()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "readable");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal("readable", (await new FileService(sandbox.Workspace).ReadFileAsync("file.txt", new())).Text);
    }
}
