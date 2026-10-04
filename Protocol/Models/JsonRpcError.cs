using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);
