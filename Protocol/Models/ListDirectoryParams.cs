using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ListDirectoryParams(
    [property: JsonPropertyName("path")] string Path);
