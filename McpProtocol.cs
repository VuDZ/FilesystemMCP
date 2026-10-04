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
    // An unattributed failure must still carry the member: JSON-RPC 2.0 puts `id: null`
    // on the wire, and FS-07 requires exactly that for a rejected frame. The reply to a
    // request keeps its own id, so only the null case is affected by the override.
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Id,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] JsonRpcError? Error);

internal sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

internal sealed record ReadFileParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("start_line")] int? StartLine,
    [property: JsonPropertyName("end_line")] int? EndLine,
    [property: JsonPropertyName("allow_large_read")] bool AllowLargeRead = false,
    [property: JsonPropertyName("max_lines")] int? MaxLines = null);

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

internal sealed record DirectoryEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type);

// Untruncated list is exactly entries. truncated/truncation_reason appear only when a cap fired
// (FS-07); null is omitted, matching the hand-built payload.
internal sealed record ListDirectoryResult(
    [property: JsonPropertyName("entries")] IReadOnlyList<DirectoryEntry> Entries,
    [property: JsonPropertyName("truncated")] bool? Truncated = null,
    [property: JsonPropertyName("truncation_reason")] string? TruncationReason = null);

// FS-10 read metadata. total_lines always describes the whole file; start_line/end_line
// describe the range actually returned (omitted when the selection is empty, because the
// contract expresses an empty selection as null rather than a fabricated 1..0 range);
// truncated means a line cap fired (never the fact that a range was requested) and
// has_more means lines exist after the returned end_line.
internal sealed record ReadFileResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("total_lines")] int TotalLines,
    [property: JsonPropertyName("start_line")] int? StartLine,
    [property: JsonPropertyName("end_line")] int? EndLine,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("has_more")] bool HasMore);

internal sealed record SearchMatch(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line);

internal sealed record SearchSkip(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("code")] string Code);

// FS-04/FS-07 search object. truncation_reason is omitted unless a budget cut the result.
internal sealed record SearchResult(
    [property: JsonPropertyName("matches")] IReadOnlyList<SearchMatch> Matches,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("incomplete")] bool Incomplete,
    [property: JsonPropertyName("skipped_count")] int SkippedCount,
    [property: JsonPropertyName("skipped")] IReadOnlyList<SearchSkip> Skipped,
    [property: JsonPropertyName("truncation_reason")] string? TruncationReason = null);

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
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("new_hash")] string NewHash,
    [property: JsonPropertyName("snippet")] string Snippet);

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

// FS-05 error object: the machine code is the API, the message is advisory and
// details stay bounded (a safe relative path and the retryable flag at most).
// truncationReason is the FS-07 addition: it preserves the cause of an already-cut
// partial result when the transport has to refuse the frame itself.
internal sealed record ToolOperationError(string Code, string Message, ToolErrorDetails? Details = null);
internal sealed record ToolErrorDetails(
    [property: JsonPropertyName("requested")] string? Requested,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("truncation_reason")] string? TruncationReason = null);
internal sealed record ErrorCorrelationData(
    [property: JsonPropertyName("correlationId")] string CorrelationId);

/// <summary>
/// Why the transport had to replace a whole frame. It rides in <c>error.data</c> rather than
/// in a tool error's <c>details</c>, because the cases that need it most — a non-tool result
/// such as a large <c>tools/list</c>, and a minimal refusal — have no tool-error envelope to
/// carry the field, and the truncation cause must still reach the client.
/// </summary>
internal sealed record TruncationReasonData(
    [property: JsonPropertyName("truncation_reason")] string TruncationReason);

internal sealed record CreateFileToolResult(string Status, string Path, string Md5, string Sha256);

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
