namespace FilesystemMcp;

internal sealed record CreateFileToolResult(string Status, string Path, string Md5, string Sha256);
