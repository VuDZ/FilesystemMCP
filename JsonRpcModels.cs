using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

internal sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
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

internal sealed record WriteResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] string Status);
