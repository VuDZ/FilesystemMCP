using System.Diagnostics;
using System.Text;

namespace FilesystemMcp.Tests.Infrastructure;

internal sealed class Sandbox : IDisposable
{
    private readonly List<string> _links = [];
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "filesystemmcp-tests-" + Guid.NewGuid().ToString("N"));
    public string Workspace { get; }
    public string Outside { get; }
    public static UTF8Encoding Utf8 { get; } = new(false, true);

    public Sandbox()
    {
        Workspace = Path.Combine(Root, "Проект с пробелами 中文 😀");
        Outside = Path.Combine(Root, "outside");
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Outside);
    }

    public string Write(string relativePath, string content, Encoding? encoding = null)
    {
        var path = Path.GetFullPath(relativePath, Workspace);
        EnsureInside(path, Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? Utf8);
        return path;
    }

    public async Task JunctionAsync(string name, string target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Junction fixture must be used with WindowsFact.");
        var link = Path.GetFullPath(name, Workspace);
        EnsureInside(link, Workspace);
        EnsureInside(target, Root);
        string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var command = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path "
            + Quote(link) + " -Target " + Quote(target) + " | Out-Null";
        var result = await ProcessRunner.PowerShellAsync(["-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))]);
        Assert.True(result.ExitCode == 0, "Junction fixture failed: " + result.Stderr);
        _links.Add(link);
    }

    public string CopyServer(string name, bool protectedHost = false)
    {
        var destination = Path.Combine(Root, name);
        Directory.CreateDirectory(destination);
        var sourceExecutable = protectedHost ? ServerProcess.ProtectedExecutable : ServerProcess.DefaultExecutable;
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(sourceExecutable)!))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        return Path.Combine(destination, Path.GetFileName(sourceExecutable));
    }

    private static void EnsureInside(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Test fixture path escaped its sandbox.");
    }

    public void Dispose()
    {
        // Verify the absolute deletion target; remove our junction entries without following them.
        EnsureInside(Root, Path.GetTempPath());
        if (!Path.GetFileName(Root).StartsWith("filesystemmcp-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected cleanup target.");
        foreach (var link in _links.AsEnumerable().Reverse())
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
        // Windows can briefly retain a copied DLL after a crashed child exits (WER/AV).
        // Retry cleanup only; these waits do not synchronize any behavioral assertions.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(500); }
        }
    }
}
