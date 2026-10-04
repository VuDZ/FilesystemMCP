using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record SearchMatch(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line);
