using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ToolCallContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);
