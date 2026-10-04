namespace FilesystemMcp;

/// <summary>
/// The FS-05 machine-readable code set. A code is the stable API surface; the
/// accompanying message is advisory and never has to be parsed by a client.
/// </summary>
internal static class ToolErrorCodes
{
    internal const string FileNotFound = "file_not_found";
    internal const string DirectoryNotFound = "directory_not_found";
    internal const string FileLocked = "file_locked";
    internal const string AccessDenied = "access_denied";
    internal const string PathOutsideWorkspace = "path_outside_workspace";
    internal const string SymlinkNotAllowed = "symlink_not_allowed";
    internal const string HashConflict = "hash_conflict";
    internal const string TargetNotFound = "target_not_found";
    internal const string FileExists = "file_exists";
    internal const string UnsupportedEncoding = "unsupported_encoding";
    internal const string BinaryFile = "binary_file";
    internal const string ResourceLimit = "resource_limit";
    internal const string Cancelled = "cancelled";
}
