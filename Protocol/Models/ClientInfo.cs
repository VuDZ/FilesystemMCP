using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ClientInfo(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version);
