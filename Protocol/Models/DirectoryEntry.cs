using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record DirectoryEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type);
