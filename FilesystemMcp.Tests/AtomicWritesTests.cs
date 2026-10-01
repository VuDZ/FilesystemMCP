using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-02")]
public sealed class AtomicWritesTests
{
    [Fact, Trait("Status", "Baseline")]
    public async Task CancelledWritePreservesOriginalBytes()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "valuable original\r\n");
        var original = await File.ReadAllBytesAsync(path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileTextHelper.WriteUtf8WithoutBomAsync(path, "replacement", cancelled.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task StaleHashDoesNotOverwriteExternalChange()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old");
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("file.txt", new());
        await File.WriteAllTextAsync(path, "external", Sandbox.Utf8);
        var error = await Assert.ThrowsAsync<MutationException>(() => files.ReplaceInFileAsync("file.txt", "old", "new", read.Sha256));
        Assert.Equal("hash_conflict", error.Code);
        Assert.Equal("external", await File.ReadAllTextAsync(path));
    }
}
