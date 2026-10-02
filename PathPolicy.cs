namespace FilesystemMcp;

internal sealed class PathPolicyException(string code, string message) : UnauthorizedAccessException(message)
{
    public string Code { get; } = code;
}

internal sealed class PathPolicy
{
    public string Root { get; }
    public string LogicalRoot { get; }
    public ServerOptions Options { get; }

    /// <summary>
    /// The immutable FS-07 budgets. Exposed here because the writer must refuse an oversized
    /// result before its commit point, and the policy is what every mutation entry point
    /// already carries.
    /// </summary>
    public ResourceBudget Budget => Options.Budget;
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal Action? BeforeWriteCommit { get; set; }
    internal Action? AfterWriteParentsPinned { get; set; }
    internal AtomicWriteDependencies AtomicWrites { get; set; } = new();

    public PathPolicy(string workspaceRoot, ServerOptions? options = null)
    {
        Options = options ?? new();
        LogicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (!Options.AllowSymLinks && IsLink(LogicalRoot)) throw Error("symlink_not_allowed");
        var physicalRoot = WalkAbsolute(LogicalRoot, true, new HashSet<string>(Comparer), 0);
        if (!Directory.Exists(physicalRoot)) throw new DirectoryNotFoundException("Workspace directory not found.");
        Root = CanonicalDirectory(physicalRoot);
    }

    public string Resolve(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) throw new ArgumentException("Path must be provided.");
        if (!Options.AllowSymLinks && IsLink(LogicalRoot)) throw Error("symlink_not_allowed");
        var relative = requested;
        if (Path.IsPathRooted(requested))
        {
            if (!Contains(LogicalRoot, requested)) throw Error("path_outside_workspace");
            relative = requested[LogicalRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        // Input depth is checked separately from physical traversal. Reject even
        // a transient logical escape; never collapse link/.. before resolving it.
        var inputDepth = 0;
        foreach (var part in Parts(relative))
        {
            ValidateComponent(part);
            if (part == ".") continue;
            if (part == "..")
            {
                if (--inputDepth < 0) throw Error("path_outside_workspace");
            }
            else inputDepth++;
        }
        var liveRoot = CanonicalDirectory(WalkAbsolute(LogicalRoot, Options.AllowSymLinks, new HashSet<string>(Comparer), 0, LogicalRoot));
        if (!Comparer.Equals(liveRoot, Root)) throw Error("path_outside_workspace");
        var resolved = Walk(Root, Parts(relative), Options.AllowSymLinks, new HashSet<string>(Comparer), 0,
            jail: Options.AllowSymLinks ? null : Root);
        if (!Path.IsPathFullyQualified(resolved)) throw Error("path_outside_workspace");
        if (!Options.AllowSymLinks && !Contains(Root, resolved)) throw Error("path_outside_workspace");
        return resolved;
    }

    internal static bool Contains(string root, string path) => Comparer.Equals(root, path)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string[] Parts(string path) => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static string WalkAbsolute(string path, bool allow, HashSet<string> links, int depth, string? enforceLinksFrom = null)
    {
        if (!Path.IsPathFullyQualified(path)) throw Error("path_outside_workspace");
        var volume = Path.GetPathRoot(path)!;
        return Walk(volume, Parts(path[volume.Length..]), allow, links, depth, enforceLinksFrom);
    }

    private static string Walk(string current, IEnumerable<string> parts, bool allow, HashSet<string> links, int depth, string? enforceLinksFrom = null, string? jail = null)
    {
        foreach (var part in parts)
        {
            ValidateComponent(part);
            if (part == ".") continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)) ?? current;
                if (jail is not null && !Contains(jail, current)) throw Error("path_outside_workspace");
                continue;
            }
            current = Path.Combine(current, part);
            if (!IsLink(current)) continue;
            if (!allow && (enforceLinksFrom is null || Contains(enforceLinksFrom, current))) throw Error("symlink_not_allowed");
            if (depth >= 64 || !links.Add(current)) throw Error("symlink_cycle");
            NativePath.CheckReparseType(current);
            FileSystemInfo info = new FileInfo(current);
            var target = info.LinkTarget;
            if (target is null) throw Error("unsupported_reparse_point");
            var targetPath = Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(current)!, target);
            current = WalkAbsolute(targetPath, true, links, depth + 1);
            if (!File.Exists(current) && !Directory.Exists(current)) throw Error("symlink_dangling");
            if (Directory.Exists(current)) current = CanonicalDirectory(current);
            if (jail is not null && !Contains(jail, current)) throw Error("path_outside_workspace");
            links.Remove(info.FullName);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static void ValidateComponent(string part)
    {
        // A drive-qualified component makes Path.Combine discard its base on
        // Windows. Colons remain valid filename characters on Unix.
        if (OperatingSystem.IsWindows() && (part.Contains(':') || Path.IsPathRooted(part)))
            throw Error("path_outside_workspace");
    }

    private static string CanonicalDirectory(string path) => OperatingSystem.IsWindows()
        ? NativePath.GetPhysicalDirectoryPath(path) : path;

    internal static PathPolicyException Error(string code) => new(code, code switch
    {
        "path_outside_workspace" => "Path resolves outside the workspace.",
        "symlink_not_allowed" => "Following links is disabled.",
        "symlink_cycle" => "Symbolic link cycle or excessive link depth.",
        "symlink_dangling" => "Symbolic link target does not exist.",
        "unsupported_reparse_point" => "Unsupported filesystem redirection.",
        "path_is_directory" => "A file path is required; the requested path is a directory.",
        _ => "Path changed or cannot be safely accessed."
    });
}
