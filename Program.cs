using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FilesystemMcp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            await Console.Error.WriteLineAsync("WorkspaceRoot argument is required.");
            return 1;
        }

        var workspaceRoot = args[0];
        try
        {
            _ = Path.GetFullPath(workspaceRoot);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Invalid WorkspaceRoot: {ex.Message}");
            return 1;
        }

        var fileService = new FileService(workspaceRoot);
        var mutationService = new MutationService(workspaceRoot);
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new ReadFileTool(fileService));
        toolRegistry.Register(new ReplaceInFileTool(fileService));

        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonRpcRequest? request = null;
            JsonRpcResponse? response = null;

            try
            {
                request = JsonSerializer.Deserialize(line, McpJsonContext.Default.JsonRpcRequest);
                response = await ProcessRequestAsync(request, fileService, mutationService, workspaceRoot, toolRegistry);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(ex.ToString());
                var code = ex is JsonException ? -32700 : -32603;
                var message = ex is JsonException ? "Parse error" : "Internal error";
                response = CreateErrorResponse(
                    id: request?.Id,
                    code: code,
                    message: message);
            }

            if (response is null || response.Id is null || response.Id.Value.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            var json = JsonSerializer.Serialize(response, McpJsonContext.Default.JsonRpcResponse);
            await Console.Out.WriteLineAsync(json);
            await Console.Out.FlushAsync();
        }
    }

    private static async Task<JsonRpcResponse?> ProcessRequestAsync(
        JsonRpcRequest? request,
        FileService fileService,
        MutationService mutationService,
        string workspaceRoot,
        ToolRegistry toolRegistry)
    {
        if (request is null)
        {
            return CreateErrorResponse(
                id: null,
                code: -32700,
                message: "Parse error");
        }

        if (!string.Equals(request.JsonRpc, JsonRpcConstants.Version, StringComparison.Ordinal))
        {
            return CreateErrorResponse(
                id: request.Id,
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

        return request.Method switch
        {
            "tools/list" => HandleToolsList(toolRegistry, request.Id),
            "tools/call" => await HandleToolsCallAsync(request, toolRegistry),
            "read_file" => await HandleReadFileAsync(request, fileService),
            "create_file" => await HandleCreateFileAsync(request, mutationService),
            "replace_in_file" => await HandleReplaceInFileAsync(request, mutationService),
            "list_directory" => HandleListDirectoryStub(request, workspaceRoot),
            "search" => HandleSearchStub(request),
            "append_to_file" => HandleAppendToFileStub(request, workspaceRoot),
            _ => CreateErrorResponse(request.Id, -32601, "Method not found")
        };
    }

    private static JsonRpcResponse HandleToolsList(ToolRegistry toolRegistry, JsonElement? id)
    {
        var payload = toolRegistry.GetToolsListAsJson();
        return CreateResultResponse(id, payload);
    }

    private static async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, ToolRegistry toolRegistry)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.ToolsCallParams);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Name))
        {
            return CreateErrorResponse(request.Id, -32602, "Missing or invalid tools/call params.");
        }

        var arguments = parameters.Arguments is null
            || parameters.Arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? EmptyObject()
            : parameters.Arguments.Value;

        var toolResult = await toolRegistry.ExecuteToolAsync(parameters.Name, arguments);
        var result = new ToolsCallResult(
            Content: new[] { new ToolCallContent("text", toolResult) },
            IsError: false);

        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ToolsCallResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static async Task<JsonRpcResponse> HandleReadFileAsync(JsonRpcRequest request, FileService fileService)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.ReadFileParams);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Path))
        {
            return CreateErrorResponse(request.Id, -32602, "Missing or invalid read_file params.");
        }

        var result = await fileService.ReadFileAsync(parameters.Path, parameters.StartLine, parameters.EndLine);
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ReadFileResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static async Task<JsonRpcResponse> HandleCreateFileAsync(JsonRpcRequest request, MutationService mutationService)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.CreateFileParams);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Path))
        {
            return CreateErrorResponse(request.Id, -32602, "Missing or invalid create_file params.");
        }

        var result = await mutationService.CreateFileAsync(parameters.Path, parameters.Content ?? string.Empty);
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.CreateFileResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static async Task<JsonRpcResponse> HandleReplaceInFileAsync(JsonRpcRequest request, MutationService mutationService)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.ReplaceInFileParams);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Path))
        {
            return CreateErrorResponse(request.Id, -32602, "Missing or invalid replace_in_file params.");
        }

        var result = await mutationService.ReplaceInFileAsync(
            parameters.Path,
            parameters.TargetSnippet ?? string.Empty,
            parameters.ReplacementSnippet ?? string.Empty,
            parameters.OriginalHash ?? string.Empty);

        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ReplaceInFileResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static JsonRpcResponse HandleListDirectoryStub(JsonRpcRequest request, string workspaceRoot)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.ListDirectoryParams);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Path))
        {
            return CreateErrorResponse(request.Id, -32602, "Missing or invalid list_directory params.");
        }

        var fullPath = WorkspaceJail.ResolvePath(workspaceRoot, parameters.Path);
        var result = new ListDirectoryResult(fullPath, Array.Empty<string>());
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ListDirectoryResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static JsonRpcResponse HandleSearchStub(JsonRpcRequest request)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.SearchParams);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Regex) || string.IsNullOrWhiteSpace(parameters.FileMask))
        {
            return CreateErrorResponse(request.Id, -32602, "Missing or invalid search params.");
        }

        var result = new SearchResult(Array.Empty<string>());
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.SearchResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static JsonRpcResponse HandleAppendToFileStub(JsonRpcRequest request, string workspaceRoot)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.AppendToFileParams);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Path))
        {
            return CreateErrorResponse(request.Id, -32602, "Missing or invalid append_to_file params.");
        }

        var fullPath = WorkspaceJail.ResolvePath(workspaceRoot, parameters.Path);
        var result = new WriteResult(fullPath, "stub");
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.WriteResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static TParams? DeserializeParams<TParams>(JsonElement? paramsNode, JsonTypeInfo<TParams> typeInfo)
    {
        if (paramsNode is null || paramsNode.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        if (paramsNode.Value.ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException("params must be a JSON object.");
        }

        return paramsNode.Value.Deserialize(typeInfo);
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonRpcResponse CreateResultResponse(JsonElement? id, JsonElement result) =>
        new(JsonRpcConstants.Version, id, result, null);

    private static JsonRpcResponse CreateErrorResponse(JsonElement? id, int code, string message) =>
        new(JsonRpcConstants.Version, id, null, new JsonRpcError(code, message));

}
