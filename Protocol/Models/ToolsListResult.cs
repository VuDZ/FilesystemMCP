using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ToolsListResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<ToolDefinition> Tools);
