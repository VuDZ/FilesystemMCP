namespace FilesystemMcp;

internal sealed class PathPolicyException(string code, string message) : UnauthorizedAccessException(message)
{
    public string Code { get; } = code;
}
