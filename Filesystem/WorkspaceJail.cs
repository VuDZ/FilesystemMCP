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

        return new PathPolicy(workspaceRoot).Resolve(requestedPath);
    }
}
