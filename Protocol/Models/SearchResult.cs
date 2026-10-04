using System.Text.Json.Serialization;

namespace FilesystemMcp;

// FS-04/FS-07 search object. truncation_reason is omitted unless a budget cut the result.
internal sealed record SearchResult(
    [property: JsonPropertyName("matches")] IReadOnlyList<SearchMatch> Matches,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("incomplete")] bool Incomplete,
    [property: JsonPropertyName("skipped_count")] int SkippedCount,
    [property: JsonPropertyName("skipped")] IReadOnlyList<SearchSkip> Skipped,
    [property: JsonPropertyName("truncation_reason")] string? TruncationReason = null);
