using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ReplaceInFileParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("target_snippet")] string TargetSnippet,
    [property: JsonPropertyName("replacement_snippet")] string ReplacementSnippet,
    [property: JsonPropertyName("original_hash")] string OriginalHash);
