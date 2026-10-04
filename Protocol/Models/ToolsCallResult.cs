using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ToolsCallResult(
    [property: JsonPropertyName("content")] IReadOnlyList<ToolCallContent> Content,
    [property: JsonPropertyName("isError")] bool IsError);
