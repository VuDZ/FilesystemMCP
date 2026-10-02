using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FilesystemMcp;

internal static class Program
{
    internal static AtomicWriteDependencies? AtomicWritesForHost { get; set; }
    private const string DefaultProtocolVersion = "2024-11-05";
    private const string ServerName = "FilesystemMCP";
    private const int InvalidParamsCode = -32602;
    private const int InternalErrorCode = -32603;
    private const int ParseErrorCode = -32700;
    // Legacy direct RPC methods keep the accepted FS-01/FS-04 envelope: a JSON-RPC
    // error whose message is the machine code. FS-12 removes those methods; routing
    // their classification through ToolErrorMapper keeps the code set identical to
    // tools/call without changing the accepted envelope. See docs/04-file-locks.md.
    private const int LegacyOperationalErrorCode = -32001;
    private static readonly JsonElement ServerCapabilities = ParseJsonElement("""{"tools":{"listChanged":false}}""");
    private static readonly JsonElement EmptyPromptsList = ParseJsonElement("""{"prompts":[]}""");
    private static readonly JsonElement EmptyResourcesList = ParseJsonElement("""{"resources":[]}""");

    private static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);

        PathPolicy policy;
        try
        {
            var (workspace, options) = ServerOptions.Parse(args);
            policy = new PathPolicy(workspace, options);
            if (AtomicWritesForHost is not null) policy.AtomicWrites = AtomicWritesForHost;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Startup failed: " + (ex is PathPolicyException pathError ? pathError.Code : ex.Message));
            return 1;
        }
        var fileService = new FileService(policy);
        var mutationService = new MutationService(policy);
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new ListDirectoryTool(policy));
        toolRegistry.Register(new SearchTool(policy));
        toolRegistry.Register(new CreateFileTool(policy));
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
                response = await ProcessRequestAsync(request, fileService, mutationService, policy, toolRegistry);
            }
            catch (ArgumentException ex) when (ex is not ArgumentNullException)
            {
                // Client-supplied arguments of any entry point. ArgumentNullException is
                // excluded: it is a contract check on injected services, i.e. a defect.
                response = CreateInvalidParamsResponse(request?.Id, ex);
            }
            catch (Exception ex) when (ToolErrorMapper.TryMap(ex, requestedPath: null, out var operational))
            {
                // Legacy direct RPC methods. tools/call never arrives here: it renders the
                // same mapped code as an isError result (see HandleToolsCallAsync).
                response = CreateErrorResponse(request?.Id, LegacyOperationalErrorCode, operational.Code);
            }
            catch (Exception ex)
            {
                // An unexpected defect: the client gets a neutral message and a correlation
                // id, the full exception goes to the best-effort log and never to the wire.
                var correlationId = ToolErrorMapper.NewCorrelationId();
                try { McpLogger.LogError($"Unhandled request failure (correlationId={correlationId})", ex); } catch { }
                var isParseError = ex is JsonException;
                response = CreateErrorResponse(
                    id: request?.Id,
                    code: isParseError ? ParseErrorCode : InternalErrorCode,
                    message: isParseError ? "Parse error" : "Internal error",
                    correlationId: correlationId);
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
        PathPolicy policy,
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
            "initialize" => HandleInitialize(request),
            "initialized" => null,
            "notifications/initialized" => null,
            "ping" => HandlePing(request.Id),
            "tools/list" => HandleToolsList(toolRegistry, request.Id),
            "tools/call" => await HandleToolsCallAsync(request, toolRegistry),
            "read_file" => await HandleReadFileAsync(request, fileService),
            "create_file" => await HandleCreateFileAsync(request, mutationService),
            "replace_in_file" => await HandleReplaceInFileAsync(request, mutationService),
            "list_directory" => HandleListDirectoryStub(request, policy),
            "search" => HandleSearchStub(request),
            "append_to_file" => HandleAppendToFileStub(request, policy),
            "prompts/list" => HandlePromptsList(request.Id),
            "resources/list" => HandleResourcesList(request.Id),
            _ => CreateErrorResponse(request.Id, -32601, "Method not found: " + request.Method)
        };
    }

    private static JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        var parameters = request.Params is null || request.Params.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : DeserializeParams(request.Params, McpJsonContext.Default.InitializeParams);

        var protocolVersion = string.IsNullOrWhiteSpace(parameters?.ProtocolVersion)
            ? DefaultProtocolVersion
            : parameters!.ProtocolVersion!;

        var serverInfo = new ServerInfo(ServerName, GetServerVersion());
        var result = new InitializeResult(protocolVersion, ServerCapabilities, serverInfo);
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.InitializeResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static JsonRpcResponse HandlePing(JsonElement? id) =>
        CreateResultResponse(id, EmptyObject());

    private static JsonRpcResponse HandleToolsList(ToolRegistry toolRegistry, JsonElement? id)
    {
        var payload = toolRegistry.GetToolsListAsJson();
        return CreateResultResponse(id, payload);
    }

    private static JsonRpcResponse HandlePromptsList(JsonElement? id) =>
        CreateResultResponse(id, EmptyPromptsList);

    private static JsonRpcResponse HandleResourcesList(JsonElement? id) =>
        CreateResultResponse(id, EmptyResourcesList);

    private static async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, ToolRegistry toolRegistry)
    {
        ToolsCallParams? parameters;
        try
        {
            parameters = DeserializeParams(request.Params, McpJsonContext.Default.ToolsCallParams);
        }
        catch (JsonException)
        {
            // A params node of the wrong shape is an invalid-params failure (FS-05 owns
            // tools/call argument validation); a syntactically broken frame is FS-13.
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid tools/call params.");
        }

        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Name))
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid tools/call params.");
        }

        var arguments = parameters.Arguments is null
            || parameters.Arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? EmptyObject()
            : parameters.Arguments.Value;

        ToolArguments.TryGetPath(arguments, out var requestedPath);

        try
        {
            var toolResult = await toolRegistry.ExecuteToolAsync(parameters.Name, arguments);
            var result = new ToolsCallResult(
                Content: new[] { new ToolCallContent("text", toolResult) },
                IsError: false);

            var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ToolsCallResult);
            return CreateResultResponse(request.Id, payload);
        }
        catch (UnknownToolException ex)
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, LogSanitizer.SanitizeText(ex.Message, ToolErrorMapper.MaxMessageLength));
        }
        catch (ArgumentException ex) when (ex is not ArgumentNullException)
        {
            return CreateInvalidParamsResponse(request.Id, ex);
        }
        catch (Exception ex) when (ToolErrorMapper.TryMap(ex, requestedPath, out var error))
        {
            // Expected operational failure: result.isError with one serialized
            // {code,message,details?} object. Never built by string concatenation.
            return CreateToolErrorResponse(request.Id, error);
        }

        // Any other failure is an unexpected defect: it falls through to the single
        // -32603 correlation path in Main, which logs it and never echoes it.
    }

    private static JsonRpcResponse CreateToolErrorResponse(JsonElement? id, ToolOperationError error)
    {
        var text = JsonSerializer.Serialize(error, McpJsonContext.Default.ToolOperationError);
        var result = new ToolsCallResult(
            Content: new[] { new ToolCallContent("text", text) },
            IsError: true);
        return CreateResultResponse(id, JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ToolsCallResult));
    }

    /// <summary>
    /// Invalid-params reply. The client gets a fixed product message; the bounded,
    /// sanitized .NET argument text goes to the best-effort log instead, because that
    /// text is platform text this server does not own (a future framework message could
    /// embed a host path). Code -32602 is the machine-readable part.
    /// </summary>
    private static JsonRpcResponse CreateInvalidParamsResponse(JsonElement? id, Exception exception)
    {
        try { McpLogger.LogInfo("Invalid arguments: " + ToolErrorMapper.ArgumentFailureDetail(exception)); } catch { }
        return CreateErrorResponse(id, InvalidParamsCode, "Invalid arguments.");
    }

    private static async Task<JsonRpcResponse> HandleReadFileAsync(JsonRpcRequest request, FileService fileService)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.ReadFileParams);
        if (parameters is null || !TryResolvePathParam(request.Params, out var path))
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid read_file params.");
        }

        var options = new ReadFileOptions(
            StartLine: parameters.StartLine,
            EndLine: parameters.EndLine,
            AllowLargeRead: parameters.AllowLargeRead,
            MaxLines: parameters.MaxLines);
        var result = await fileService.ReadFileAsync(path, options);
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ReadFileResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static async Task<JsonRpcResponse> HandleCreateFileAsync(JsonRpcRequest request, MutationService mutationService)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.CreateFileParams);
        if (parameters is null || !TryResolvePathParam(request.Params, out var path))
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid create_file params.");
        }

        var result = await mutationService.CreateFileAsync(path, parameters.Content ?? string.Empty);
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.CreateFileResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static async Task<JsonRpcResponse> HandleReplaceInFileAsync(JsonRpcRequest request, MutationService mutationService)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.ReplaceInFileParams);
        if (parameters is null || !TryResolvePathParam(request.Params, out var path))
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid replace_in_file params.");
        }

        var result = await mutationService.ReplaceInFileAsync(
            path,
            parameters.TargetSnippet ?? string.Empty,
            parameters.ReplacementSnippet ?? string.Empty,
            parameters.OriginalHash ?? string.Empty);

        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ReplaceInFileResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static JsonRpcResponse HandleListDirectoryStub(JsonRpcRequest request, PathPolicy policy)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.ListDirectoryParams);
        if (parameters is null || !TryResolvePathParam(request.Params, out var path))
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid list_directory params.");
        }

        var fullPath = policy.Resolve(path);
        var result = new ListDirectoryResult(fullPath, Array.Empty<string>());
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.ListDirectoryResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static JsonRpcResponse HandleSearchStub(JsonRpcRequest request)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.SearchParams);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Regex) || string.IsNullOrWhiteSpace(parameters.FileMask))
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid search params.");
        }

        var result = new SearchResult(Array.Empty<string>());
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.SearchResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static JsonRpcResponse HandleAppendToFileStub(JsonRpcRequest request, PathPolicy policy)
    {
        var parameters = DeserializeParams(request.Params, McpJsonContext.Default.AppendToFileParams);
        if (parameters is null || !TryResolvePathParam(request.Params, out var path))
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid append_to_file params.");
        }

        var fullPath = policy.Resolve(path);
        var result = new WriteResult(fullPath, "stub");
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.WriteResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static bool TryResolvePathParam(JsonElement? paramsNode, out string path)
    {
        path = string.Empty;
        if (paramsNode is null || paramsNode.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (paramsNode.Value.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        return ToolArguments.TryGetPath(paramsNode.Value, out path);
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

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string GetServerVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(version) ? "1.0.0" : version;
    }

    private static JsonRpcResponse CreateResultResponse(JsonElement? id, JsonElement result) =>
        new(JsonRpcConstants.Version, id, result, null);

    private static JsonRpcResponse CreateErrorResponse(JsonElement? id, int code, string message, string? correlationId = null) =>
        new(JsonRpcConstants.Version, id, null, new JsonRpcError(
            code,
            message,
            correlationId is null
                ? null
                : JsonSerializer.SerializeToElement(new ErrorCorrelationData(correlationId), McpJsonContext.Default.ErrorCorrelationData)));

}
