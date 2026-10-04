using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ToolErrorDetails(
    [property: JsonPropertyName("requested")] string? Requested,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("truncation_reason")] string? TruncationReason = null);
