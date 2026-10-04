using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);
