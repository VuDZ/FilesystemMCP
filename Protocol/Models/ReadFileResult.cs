using System.Text.Json.Serialization;

namespace FilesystemMcp;

// FS-10 read metadata. total_lines always describes the whole file; start_line/end_line
// describe the range actually returned (omitted when the selection is empty, because the
// contract expresses an empty selection as null rather than a fabricated 1..0 range);
// truncated means a line cap fired (never the fact that a range was requested) and
// has_more means lines exist after the returned end_line.
internal sealed record ReadFileResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("total_lines")] int TotalLines,
    [property: JsonPropertyName("start_line")] int? StartLine,
    [property: JsonPropertyName("end_line")] int? EndLine,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("has_more")] bool HasMore);
