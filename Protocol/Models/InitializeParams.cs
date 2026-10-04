using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record InitializeParams(
    [property: JsonPropertyName("protocolVersion")] string? ProtocolVersion,
    [property: JsonPropertyName("capabilities")] JsonElement? Capabilities,
    [property: JsonPropertyName("clientInfo")] ClientInfo? ClientInfo);
