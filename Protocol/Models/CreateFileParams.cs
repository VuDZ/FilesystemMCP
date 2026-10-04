using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record CreateFileParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);
