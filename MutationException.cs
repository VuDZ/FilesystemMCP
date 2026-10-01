namespace FilesystemMcp;

internal sealed class MutationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    internal static MutationException Conflict() => new("hash_conflict", "File changed. Read the file again before patching.");
    internal static MutationException Exists() => new("file_exists", "File already exists. Use replace_in_file.");
}
