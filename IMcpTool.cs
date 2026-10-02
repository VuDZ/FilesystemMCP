using System.Text.Json;

namespace FilesystemMcp;

internal interface IMcpTool
{
    string Name { get; }
    string Description { get; }
    string InputSchemaJson { get; }

    /// <summary>
    /// Executes one tool invocation. The token carries both the peer's
    /// <c>notifications/cancelled</c> for this request and the FS-07 operation
    /// deadline, so an implementation must observe it on every bounded loop and
    /// inside every awaited I/O call. Returning promptly on cancellation is what
    /// keeps a mutation from committing after its deadline (FS-02 owns the commit
    /// boundary, FS-07 owns the budget above it).
    /// </summary>
    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}
