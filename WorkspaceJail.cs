namespace FilesystemMcp;

internal static class WorkspaceJail
{
    public static string ResolvePath(string workspaceRoot, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("WorkspaceRoot must be provided.", nameof(workspaceRoot));
        }

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new ArgumentException("Path must be provided.", nameof(requestedPath));
        }

        var normalizedRoot = Path.GetFullPath(workspaceRoot);
        var combined = Path.Combine(normalizedRoot, requestedPath);
        var resolved = Path.GetFullPath(combined);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(rootWithSeparator, comparison)
            && !string.Equals(resolved, normalizedRoot, comparison))
        {
            throw new UnauthorizedAccessException("Access denied: path escapes WorkspaceRoot.");
        }

        return resolved;
    }
}
