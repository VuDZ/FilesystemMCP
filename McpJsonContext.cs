using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(ListDirectoryResult))]
[JsonSerializable(typeof(ReadFileResult))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(WriteResult))]
internal partial class McpJsonContext : JsonSerializerContext
{
}
