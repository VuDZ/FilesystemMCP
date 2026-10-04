using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record ErrorCorrelationData(
    [property: JsonPropertyName("correlationId")] string CorrelationId);
