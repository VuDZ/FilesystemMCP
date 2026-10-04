namespace FilesystemMcp;

/// <summary>
/// Raised when an operation exceeds a budget that is checked before any expensive
/// materialization. It is the same FS-05 <c>resource_limit</c> outcome the existing
/// read guards produce, so a client sees one code for every budget refusal.
/// </summary>
internal sealed class ResourceLimitException(string message) : OperationalException(ToolErrorCodes.ResourceLimit, message);
