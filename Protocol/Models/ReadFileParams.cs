using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ReadFileParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("start_line")] int? StartLine,
    [property: JsonPropertyName("end_line")] int? EndLine,
    [property: JsonPropertyName("allow_large_read")] bool AllowLargeRead = false,
    [property: JsonPropertyName("max_lines")] int? MaxLines = null);
