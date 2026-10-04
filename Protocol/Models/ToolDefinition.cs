using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema);
