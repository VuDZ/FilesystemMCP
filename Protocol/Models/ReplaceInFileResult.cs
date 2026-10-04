using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ReplaceInFileResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("sha256")] string Sha256);
