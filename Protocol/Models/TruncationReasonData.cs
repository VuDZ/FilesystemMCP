using System.Text.Json.Serialization;

namespace FilesystemMcp;

/// <summary>
/// Why the transport had to replace a whole frame. It rides in <c>error.data</c> rather than
/// in a tool error's <c>details</c>, because the cases that need it most — a non-tool result
/// such as a large <c>tools/list</c>, and a minimal refusal — have no tool-error envelope to
/// carry the field, and the truncation cause must still reach the client.
/// </summary>
internal sealed record TruncationReasonData(
    [property: JsonPropertyName("truncation_reason")] string TruncationReason);
