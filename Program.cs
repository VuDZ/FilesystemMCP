using System.Text;
using System.Text.Json;

namespace FilesystemMcp;

internal static class Program
{
    private const string JsonRpcVersion = "2.0";
    private const int MaxHeaderBytes = 16 * 1024;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            await Console.Error.WriteLineAsync("WorkspaceRoot argument is required.");
            return 1;
        }

        WorkspacePathGuard pathGuard;
        try
        {
            pathGuard = new WorkspacePathGuard(args[0]);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Invalid WorkspaceRoot: {ex.Message}");
            return 1;
        }

        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();

        while (true)
        {
            var payload = await ReadMessageAsync(input);
            if (payload is null)
            {
                return 0;
            }

            JsonRpcResponse? response;
            try
            {
                response = await HandlePayloadAsync(payload, pathGuard);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Unhandled processing error: {ex}");
                response = CreateErrorResponse(
                    id: null,
                    code: -32603,
                    message: "Internal error");
            }

            if (response is null || response.Id is null)
            {
                continue;
            }

            await WriteMessageAsync(output, response);
        }
    }

    private static async Task<JsonRpcResponse?> HandlePayloadAsync(byte[] payload, WorkspacePathGuard pathGuard)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(payload, McpJsonContext.Default.JsonRpcRequest);
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"JSON parse error: {ex.Message}");
            return CreateErrorResponse(
                id: null,
                code: -32700,
                message: "Parse error");
        }

        if (request is null || !string.Equals(request.JsonRpc, JsonRpcVersion, StringComparison.Ordinal))
        {
            return CreateErrorResponse(
                id: request?.Id,
                code: -32600,
                message: "Invalid Request");
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return CreateErrorResponse(
                id: request.Id,
                code: -32600,
                message: "Method is required");
        }

        if (!TryGetParams(request.Params, out var parameters))
        {
            return CreateErrorResponse(
                id: request.Id,
                code: -32602,
                message: "Invalid params");
        }

        return request.Method switch
        {
            "list_directory" => HandleListDirectory(request.Id, parameters, pathGuard),
            "read_file" => HandleReadFile(request.Id, parameters, pathGuard),
            "search" => HandleSearch(request.Id, parameters, pathGuard),
            "create_file" => HandleCreateFile(request.Id, parameters, pathGuard),
            "replace_in_file" => HandleReplaceInFile(request.Id, parameters, pathGuard),
            "append_to_file" => HandleAppendToFile(request.Id, parameters, pathGuard),
            _ => CreateErrorResponse(request.Id, -32601, "Method not found")
        };
    }

    private static JsonRpcResponse HandleListDirectory(
        JsonElement? id,
        Dictionary<string, JsonElement> parameters,
        WorkspacePathGuard pathGuard)
    {
        if (!TryGetRequiredString(parameters, "path", out var path))
        {
            return CreateErrorResponse(id, -32602, "Missing 'path'.");
        }

        if (!pathGuard.TryResolvePath(path, out var fullPath))
        {
            return CreateErrorResponse(id, -32001, "Path escapes WorkspaceRoot.");
        }

        var result = new ListDirectoryResult(fullPath, Array.Empty<string>());
        return CreateResultResponse(id, JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ListDirectoryResult));
    }

    private static JsonRpcResponse HandleReadFile(
        JsonElement? id,
        Dictionary<string, JsonElement> parameters,
        WorkspacePathGuard pathGuard)
    {
        if (!TryGetRequiredString(parameters, "path", out var path))
        {
            return CreateErrorResponse(id, -32602, "Missing 'path'.");
        }

        if (!pathGuard.TryResolvePath(path, out var fullPath))
        {
            return CreateErrorResponse(id, -32001, "Path escapes WorkspaceRoot.");
        }

        // start_line/end_line accepted but not implemented in this scaffold.
        var result = new ReadFileResult(fullPath, string.Empty, string.Empty, string.Empty);
        return CreateResultResponse(id, JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ReadFileResult));
    }

    private static JsonRpcResponse HandleSearch(
        JsonElement? id,
        Dictionary<string, JsonElement> parameters,
        WorkspacePathGuard pathGuard)
    {
        if (!TryGetRequiredString(parameters, "regex", out _))
        {
            return CreateErrorResponse(id, -32602, "Missing 'regex'.");
        }

        if (!TryGetRequiredString(parameters, "file_mask", out var fileMask))
        {
            return CreateErrorResponse(id, -32602, "Missing 'file_mask'.");
        }

        if (!pathGuard.IsSafeRelativeMask(fileMask))
        {
            return CreateErrorResponse(id, -32001, "file_mask escapes WorkspaceRoot.");
        }

        var result = new SearchResult(Array.Empty<string>());
        return CreateResultResponse(id, JsonSerializer.SerializeToElement(result, McpJsonContext.Default.SearchResult));
    }

    private static JsonRpcResponse HandleCreateFile(
        JsonElement? id,
        Dictionary<string, JsonElement> parameters,
        WorkspacePathGuard pathGuard)
    {
        if (!TryGetRequiredString(parameters, "path", out var path))
        {
            return CreateErrorResponse(id, -32602, "Missing 'path'.");
        }

        if (!TryGetRequiredString(parameters, "content", out _))
        {
            return CreateErrorResponse(id, -32602, "Missing 'content'.");
        }

        if (!pathGuard.TryResolvePath(path, out var fullPath))
        {
            return CreateErrorResponse(id, -32001, "Path escapes WorkspaceRoot.");
        }

        var result = new WriteResult(fullPath, "stub");
        return CreateResultResponse(id, JsonSerializer.SerializeToElement(result, McpJsonContext.Default.WriteResult));
    }

    private static JsonRpcResponse HandleReplaceInFile(
        JsonElement? id,
        Dictionary<string, JsonElement> parameters,
        WorkspacePathGuard pathGuard)
    {
        if (!TryGetRequiredString(parameters, "path", out var path))
        {
            return CreateErrorResponse(id, -32602, "Missing 'path'.");
        }

        if (!TryGetRequiredString(parameters, "target_snippet", out _)
            || !TryGetRequiredString(parameters, "replacement_snippet", out _)
            || !TryGetRequiredString(parameters, "original_hash", out _))
        {
            return CreateErrorResponse(
                id,
                -32602,
                "Missing one or more required params: 'target_snippet', 'replacement_snippet', 'original_hash'.");
        }

        if (!pathGuard.TryResolvePath(path, out var fullPath))
        {
            return CreateErrorResponse(id, -32001, "Path escapes WorkspaceRoot.");
        }

        var result = new WriteResult(fullPath, "stub");
        return CreateResultResponse(id, JsonSerializer.SerializeToElement(result, McpJsonContext.Default.WriteResult));
    }

    private static JsonRpcResponse HandleAppendToFile(
        JsonElement? id,
        Dictionary<string, JsonElement> parameters,
        WorkspacePathGuard pathGuard)
    {
        if (!TryGetRequiredString(parameters, "path", out var path))
        {
            return CreateErrorResponse(id, -32602, "Missing 'path'.");
        }

        if (!TryGetRequiredString(parameters, "content", out _))
        {
            return CreateErrorResponse(id, -32602, "Missing 'content'.");
        }

        if (!pathGuard.TryResolvePath(path, out var fullPath))
        {
            return CreateErrorResponse(id, -32001, "Path escapes WorkspaceRoot.");
        }

        var result = new WriteResult(fullPath, "stub");
        return CreateResultResponse(id, JsonSerializer.SerializeToElement(result, McpJsonContext.Default.WriteResult));
    }

    private static bool TryGetParams(JsonElement? paramsNode, out Dictionary<string, JsonElement> parameters)
    {
        if (paramsNode is null || paramsNode.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            return true;
        }

        if (paramsNode.Value.ValueKind != JsonValueKind.Object)
        {
            parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            return false;
        }

        var parsed = paramsNode.Value.Deserialize(McpJsonContext.Default.DictionaryStringJsonElement);
        parameters = parsed ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return true;
    }

    private static bool TryGetRequiredString(
        IReadOnlyDictionary<string, JsonElement> parameters,
        string propertyName,
        out string value)
    {
        if (parameters.TryGetValue(propertyName, out var node)
            && node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static JsonRpcResponse CreateResultResponse(JsonElement? id, JsonElement result) =>
        new(JsonRpcVersion, id, result, null);

    private static JsonRpcResponse CreateErrorResponse(JsonElement? id, int code, string message) =>
        new(JsonRpcVersion, id, null, new JsonRpcError(code, message));

    private static async Task<byte[]?> ReadMessageAsync(Stream input)
    {
        var header = await ReadHeaderAsync(input);
        if (header is null)
        {
            return null;
        }

        if (!TryParseContentLength(header, out var contentLength) || contentLength <= 0)
        {
            throw new InvalidDataException("Missing or invalid Content-Length header.");
        }

        var payload = new byte[contentLength];
        await ReadExactlyAsync(input, payload);
        return payload;
    }

    private static async Task<string?> ReadHeaderAsync(Stream input)
    {
        var bytes = new List<byte>(256);
        var buffer = new byte[1];

        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0)
            {
                if (bytes.Count == 0)
                {
                    return null;
                }

                throw new EndOfStreamException("Unexpected EOF while reading headers.");
            }

            bytes.Add(buffer[0]);
            if (bytes.Count > MaxHeaderBytes)
            {
                throw new InvalidDataException("Header exceeds max length.");
            }

            var count = bytes.Count;
            if (count >= 4
                && bytes[count - 4] == '\r'
                && bytes[count - 3] == '\n'
                && bytes[count - 2] == '\r'
                && bytes[count - 1] == '\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }
    }

    private static bool TryParseContentLength(string header, out int contentLength)
    {
        contentLength = 0;

        var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            const string key = "Content-Length:";
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[key.Length..].Trim();
            return int.TryParse(value, out contentLength);
        }

        return false;
    }

    private static async Task ReadExactlyAsync(Stream input, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading payload.");
            }

            offset += read;
        }
    }

    private static async Task WriteMessageAsync(Stream output, JsonRpcResponse response)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, McpJsonContext.Default.JsonRpcResponse);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");

        await output.WriteAsync(header);
        await output.WriteAsync(payload);
        await output.FlushAsync();
    }
}

internal sealed class WorkspacePathGuard
{
    private readonly string _root;
    private readonly string _rootWithSeparator;

    public WorkspacePathGuard(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("workspaceRoot cannot be empty.", nameof(workspaceRoot));
        }

        _root = Path.GetFullPath(workspaceRoot);
        _rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
    }

    public bool TryResolvePath(string inputPath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return false;
        }

        try
        {
            var candidate = Path.IsPathRooted(inputPath)
                ? inputPath
                : Path.Combine(_root, inputPath);

            var normalized = Path.GetFullPath(candidate);
            if (!IsInsideRoot(normalized))
            {
                return false;
            }

            fullPath = normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsSafeRelativeMask(string mask)
    {
        if (string.IsNullOrWhiteSpace(mask) || Path.IsPathRooted(mask))
        {
            return false;
        }

        var segments = mask
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    private bool IsInsideRoot(string fullPath) =>
        string.Equals(fullPath, _root, StringComparison.OrdinalIgnoreCase)
        || fullPath.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase);
}
