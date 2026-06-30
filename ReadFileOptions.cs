namespace FilesystemMcp;

internal sealed record ReadFileOptions(
    int? StartLine = null,
    int? EndLine = null,
    bool AllowLargeRead = false,
    int? MaxLines = null);
