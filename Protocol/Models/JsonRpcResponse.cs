using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    // An unattributed failure must still carry the member: JSON-RPC 2.0 puts `id: null`
    // on the wire, and FS-07 requires exactly that for a rejected frame. The reply to a
    // request keeps its own id, so only the null case is affected by the override.
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Id,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] JsonRpcError? Error);
