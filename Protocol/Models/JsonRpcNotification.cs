using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record JsonRpcNotification(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);
