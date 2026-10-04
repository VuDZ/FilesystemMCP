namespace FilesystemMcp;

/// <summary>
/// Unknown tool name. FS-05 treats it as a JSON-RPC invalid-params failure, never as
/// an operational <c>isError</c> result. Derived from <see cref="ArgumentException"/>
/// so every boundary that rejects client-supplied arguments rejects this too.
/// </summary>
internal sealed class UnknownToolException(string toolName) : ArgumentException("Unknown tool: '" + toolName + "'");
