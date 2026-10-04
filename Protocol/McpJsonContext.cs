using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(ReadFileParams))]
[JsonSerializable(typeof(CreateFileParams))]
[JsonSerializable(typeof(ReplaceInFileParams))]
[JsonSerializable(typeof(ListDirectoryParams))]
[JsonSerializable(typeof(SearchParams))]
[JsonSerializable(typeof(ToolsCallParams))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(ClientInfo))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(DirectoryEntry))]
[JsonSerializable(typeof(ListDirectoryResult))]
[JsonSerializable(typeof(ReadFileResult))]
[JsonSerializable(typeof(SearchMatch))]
[JsonSerializable(typeof(SearchSkip))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(CreateFileResult))]
[JsonSerializable(typeof(ReplaceInFileResult))]
[JsonSerializable(typeof(ReplaceInFileToolResult))]
[JsonSerializable(typeof(ToolDefinition))]
[JsonSerializable(typeof(ToolsListResult))]
[JsonSerializable(typeof(ToolCallContent))]
[JsonSerializable(typeof(ToolsCallResult))]
[JsonSerializable(typeof(ToolOperationError))]
[JsonSerializable(typeof(ToolErrorDetails))]
[JsonSerializable(typeof(ErrorCorrelationData))]
[JsonSerializable(typeof(TruncationReasonData))]
[JsonSerializable(typeof(CreateFileToolResult))]
internal partial class McpJsonContext : JsonSerializerContext
{
}
