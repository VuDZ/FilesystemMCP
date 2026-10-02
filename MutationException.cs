namespace FilesystemMcp;

internal sealed class MutationException : InvalidOperationException
{
    public string Code { get; }

    public MutationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public MutationException(string code, string message, Exception innerException) : base(message, innerException)
    {
        Code = code;
    }

    internal static MutationException Conflict() => new("hash_conflict", "File changed. Read the file again before patching.");
    internal static MutationException Exists() => new("file_exists", "File already exists. Use replace_in_file.");
}
