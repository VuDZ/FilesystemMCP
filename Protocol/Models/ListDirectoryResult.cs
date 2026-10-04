using System.Text.Json.Serialization;

namespace FilesystemMcp;

// Untruncated list is exactly entries. truncated/truncation_reason appear only when a cap fired
// (FS-07); null is omitted, matching the hand-built payload.
internal sealed record ListDirectoryResult(
    [property: JsonPropertyName("entries")] IReadOnlyList<DirectoryEntry> Entries,
    [property: JsonPropertyName("truncated")] bool? Truncated = null,
    [property: JsonPropertyName("truncation_reason")] string? TruncationReason = null);
