using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record SearchParams(
    [property: JsonPropertyName("regex")] string Regex,
    [property: JsonPropertyName("file_mask")] string FileMask);
