using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ReplaceInFileToolResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("new_hash")] string NewHash,
    [property: JsonPropertyName("snippet")] string Snippet);
