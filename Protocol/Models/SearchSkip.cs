using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record SearchSkip(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("code")] string Code);
