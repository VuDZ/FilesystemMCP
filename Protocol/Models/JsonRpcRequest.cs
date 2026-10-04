using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params)
{
    /// <summary>True when the frame contained an <c>id</c> member. Absence is a notification; JSON null is not.</summary>
    [JsonIgnore]
    public bool HasId { get; init; }

    /// <summary>True when the frame contained a <c>params</c> member, including JSON null.</summary>
    [JsonIgnore]
    public bool HasParams { get; init; }
}
