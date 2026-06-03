using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal static class JsonRpcConstants
{
    public const string Version = "2.0";
}

internal sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

internal sealed record JsonRpcNotification(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

internal sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] JsonRpcError? Error);

internal sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

internal sealed record ReadFileParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("start_line")] int? StartLine,
    [property: JsonPropertyName("end_line")] int? EndLine);

internal sealed record CreateFileParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ReplaceInFileParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("target_snippet")] string TargetSnippet,
    [property: JsonPropertyName("replacement_snippet")] string ReplacementSnippet,
    [property: JsonPropertyName("original_hash")] string OriginalHash);

internal sealed record ListDirectoryParams(
    [property: JsonPropertyName("path")] string Path);

internal sealed record SearchParams(
    [property: JsonPropertyName("regex")] string Regex,
    [property: JsonPropertyName("file_mask")] string FileMask);

internal sealed record AppendToFileParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ToolsCallParams(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] JsonElement? Arguments);

internal sealed record InitializeParams(
    [property: JsonPropertyName("protocolVersion")] string? ProtocolVersion,
    [property: JsonPropertyName("capabilities")] JsonElement? Capabilities,
    [property: JsonPropertyName("clientInfo")] ClientInfo? ClientInfo);

internal sealed record ClientInfo(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version);

internal sealed record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

internal sealed record InitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("capabilities")] JsonElement Capabilities,
    [property: JsonPropertyName("serverInfo")] ServerInfo ServerInfo);

internal sealed record ListDirectoryResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("entries")] IReadOnlyList<string> Entries);

internal sealed record ReadFileResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record SearchResult(
    [property: JsonPropertyName("matches")] IReadOnlyList<string> Matches);

internal sealed record CreateFileResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record ReplaceInFileResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record ReplaceInFileToolResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("new_hash")] string NewHash,
    [property: JsonPropertyName("snippet")] string Snippet);

internal sealed record WriteResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] string Status);

internal sealed record ToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema);

internal sealed record ToolsListResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<ToolDefinition> Tools);

internal sealed record ToolCallContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

internal sealed record ToolsCallResult(
    [property: JsonPropertyName("content")] IReadOnlyList<ToolCallContent> Content,
    [property: JsonPropertyName("isError")] bool IsError);

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
[JsonSerializable(typeof(AppendToFileParams))]
[JsonSerializable(typeof(ToolsCallParams))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(ClientInfo))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ListDirectoryResult))]
[JsonSerializable(typeof(ReadFileResult))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(CreateFileResult))]
[JsonSerializable(typeof(ReplaceInFileResult))]
[JsonSerializable(typeof(ReplaceInFileToolResult))]
[JsonSerializable(typeof(WriteResult))]
[JsonSerializable(typeof(ToolDefinition))]
[JsonSerializable(typeof(ToolsListResult))]
[JsonSerializable(typeof(ToolCallContent))]
[JsonSerializable(typeof(ToolsCallResult))]
internal partial class McpJsonContext : JsonSerializerContext
{
}
