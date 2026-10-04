using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record InitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("capabilities")] JsonElement Capabilities,
    [property: JsonPropertyName("serverInfo")] ServerInfo ServerInfo);
