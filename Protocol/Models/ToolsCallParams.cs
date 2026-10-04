using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ToolsCallParams(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] JsonElement? Arguments);
