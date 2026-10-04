namespace FilesystemMcp.Tests.Infrastructure;

public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        var root = Path.Combine(Path.GetTempPath(), "filesystemmcp-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "file"), "probe");
            File.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "file"));
            Directory.CreateSymbolicLink(Path.Combine(root, "dirlink"), root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException
            || ex is IOException && (ex.HResult & 0xffff) == 1314)
        {
            Skip = "Symbolic links unavailable on this OS/account: " + ex.GetType().Name + ". Run the symlink-capable OS lane.";
        }
        finally
        {
            if (Directory.Exists(Path.Combine(root, "dirlink")))
            {
                Directory.Delete(Path.Combine(root, "dirlink"));
            }

            File.Delete(Path.Combine(root, "link"));
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
