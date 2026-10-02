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
    private const int InvalidRequestCode = -32600;
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
        ServerOptions options;
        ResourceLimiter limiter;
        try
        {
            var (workspace, parsed) = ServerOptions.Parse(args);
            options = parsed;
            policy = new PathPolicy(workspace, options);
            if (AtomicWritesForHost is not null) policy.AtomicWrites = AtomicWritesForHost;
            // FS-07: the read-parallelism budget is part of startup validation, so an
            // impossible value can never reach the dispatcher.
            limiter = new ResourceLimiter(options.Budget.MaxConcurrentReads);
            // FS-06: diagnostics are best effort from the first record on. A logging
            // failure here can never fail startup, and an unavailable directory only
            // disables the file sink.
            try { McpLogger.Start(options.LogDirectory); } catch { }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Startup failed: " + (ex is PathPolicyException pathError ? pathError.Code : ex.Message));
            return 1;
        }

        var fileService = new FileService(policy) { Budget = options.Budget };
        var mutationService = new MutationService(policy);
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new ListDirectoryTool(policy, options.Budget));
        toolRegistry.Register(new SearchTool(policy, options.Budget));
        toolRegistry.Register(new CreateFileTool(policy));
        toolRegistry.Register(new ReadFileTool(fileService));
        toolRegistry.Register(new ReplaceInFileTool(fileService));

        // FS-07: one reader, one writer lock. Tools execute off the read path, so ping
        // and notifications/cancelled stay responsive while a tool is running.
        var transport = new StdioTransport(options.Budget);
        transport.Configure((request, cancellationToken) => HandleAsync(
            request, fileService, mutationService, policy, toolRegistry, limiter, options, transport, cancellationToken));
        try
        {
            await transport.RunAsync();
            return 0;
        }
        finally
        {
            // FS-06: bounded exit flush. Diagnostics are not allowed to delay shutdown,
            // and whatever is already queued is written before the process leaves.
            try { McpLogger.Shutdown(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    /// <summary>
    /// Request entry point. It owns the FS-07 operation deadline so every awaited path
    /// below (argument validation, tool execution, response building) is inside the
    /// budget, and turns a fired deadline into the machine-readable
    /// <c>resource_limit</c> outcome instead of a silent hang.
    /// </summary>
    private static async Task<JsonRpcResponse?> HandleAsync(
        JsonRpcRequest? request,
        FileService fileService,
        MutationService mutationService,
        PathPolicy policy,
        ToolRegistry toolRegistry,
        ResourceLimiter limiter,
        ServerOptions options,
        StdioTransport transport,
        CancellationToken sessionToken)
    {
        if (request is null)
        {
            return CreateErrorResponse(null, ParseErrorCode, "Parse error");
        }

        // A genuine notification is never answered; the transport keeps the one
        // exception the historical contract had, an unparsable frame, on its own path
        // because only it can tell whether an id was present.
        var hasId = request.Id is { } id && id.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (!hasId && !string.Equals(request.Method, "initialize", StringComparison.Ordinal))
        {
            return null;
        }

        // Two tokens, deliberately:
        //
        // * `deadline` is the token the operation observes. The transport cancels its
        //   per-request token for a peer `notifications/cancelled`, for EOF and for
        //   shutdown, and the session token carries that into the operation — so this
        //   token means "stop now", from either cause.
        // * `operationDeadline` is cancelled by the timer ALONE. It is what gets published
        //   through RequestDeadline, so a tool that can return a partial result can ask
        //   "was this the budget?" and get a truthful answer. Publishing the first token
        //   instead made every peer cancel look like a deadline, which turned a cancelled
        //   search into a successful `operation_timeout` partial result.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        using var operationDeadline = new CancellationTokenSource();
        var deadlineFired = false;
        if (options.Budget.OperationTimeoutMs > 0 && options.Budget.OperationTimeoutMs < long.MaxValue)
        {
            // Budget.Validate() already rejected anything above MaxOperationTimeoutMs, so
            // this conversion cannot overflow and cannot make CancelAfter throw. A throw
            // here would happen before the try below and would therefore answer nothing at
            // all, which is the one outcome a request must never have.
            operationDeadline.CancelAfter(TimeSpan.FromMilliseconds(options.Budget.OperationTimeoutMs));
        }

        using var deadlineWatch = operationDeadline.Token.Register(() =>
        {
            deadlineFired = true;
            try { deadline.Cancel(); } catch (ObjectDisposedException) { }
        });

        // The session side stamps itself, so "cancelled and the session did not end" stays
        // attributable to the budget even outside a tool invocation.
        var sessionEnded = sessionToken.IsCancellationRequested;
        using var sessionWatch = sessionToken.Register(() => sessionEnded = true);
        using var operationScope = RequestDeadline.Begin(operationDeadline.Token);

        try
        {
            // Both causes are passed as predicates, never as snapshots: classification
            // happens in a catch filter after the fact, so a captured bool would still hold
            // its dispatch-time value and a deadline that fired during the request would be
            // reported as a peer cancellation.
            return await ProcessRequestAsync(request, fileService, mutationService, policy, toolRegistry, limiter, deadline.Token, () => sessionEnded, () => deadlineFired);
        }
        catch (OperationCanceledException) when (IsDeadlineExceeded(deadlineFired, sessionEnded))
        {
            return CreateToolErrorResponse(request.Id, ResourceLimitError());
        }
    }

    /// <summary>
    /// The deadline fired while the session itself is still alive. Only then is the
    /// refusal attributable to the budget; otherwise the transport is already gone and
    /// there is nobody to answer.
    /// </summary>
    private static bool IsDeadlineExceeded(bool deadlineFired, bool sessionEnded) =>
        deadlineFired && !sessionEnded;

    /// <summary>
    /// The canonical FS-07 deadline refusal. It is constructed here rather than mapped
    /// from an exception, because the mapper can only see an OperationCanceledException
    /// and would report the peer-cancellation code instead.
    /// </summary>
    private static ToolOperationError ResourceLimitError() => new(
        ToolErrorCodes.ResourceLimit,
        ToolErrorMessages.ForCode(ToolErrorCodes.ResourceLimit) + " (operation timeout exceeded)",
        new ToolErrorDetails(null, ToolErrorMessages.IsRetryable(ToolErrorCodes.ResourceLimit)));

    private static async Task<JsonRpcResponse?> ProcessRequestAsync(
        JsonRpcRequest? request,
        FileService fileService,
        MutationService mutationService,
        PathPolicy policy,
        ToolRegistry toolRegistry,
        ResourceLimiter limiter,
        CancellationToken cancellationToken,
        Func<bool> sessionEnded,
        Func<bool> deadlineFired)
    {
        if (request is null)
        {
            return CreateErrorResponse(
                id: null,
                code: ParseErrorCode,
                message: "Parse error");
        }

        if (!string.Equals(request.JsonRpc, JsonRpcConstants.Version, StringComparison.Ordinal))
        {
            return CreateErrorResponse(
                id: request.Id,
                code: InvalidRequestCode,
                message: "Invalid Request");
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return CreateErrorResponse(
                id: request.Id,
                code: InvalidRequestCode,
                message: "Method is required");
        }

        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "initialized" => null,
                "notifications/initialized" => null,
                "ping" => HandlePing(request.Id),
                "tools/list" => HandleToolsList(toolRegistry, request.Id),
                "tools/call" => await HandleToolsCallAsync(request, toolRegistry, limiter, cancellationToken, sessionEnded, deadlineFired),
                "read_file" => await HandleLegacyToolCallAsync(request, "read_file", toolRegistry, limiter, cancellationToken, sessionEnded, deadlineFired),
                "create_file" => await HandleLegacyToolCallAsync(request, "create_file", toolRegistry, limiter, cancellationToken, sessionEnded, deadlineFired),
                "replace_in_file" => await HandleLegacyToolCallAsync(request, "replace_in_file", toolRegistry, limiter, cancellationToken, sessionEnded, deadlineFired),
                "list_directory" => await HandleLegacyToolCallAsync(request, "list_directory", toolRegistry, limiter, cancellationToken, sessionEnded, deadlineFired),
                "search" => await HandleLegacyToolCallAsync(request, "search", toolRegistry, limiter, cancellationToken, sessionEnded, deadlineFired),
                "append_to_file" => HandleAppendToFileStub(request, policy),
                "prompts/list" => HandlePromptsList(request.Id),
                "resources/list" => HandleResourcesList(request.Id),
                _ => CreateErrorResponse(request.Id, -32601, "Method not found: " + request.Method)
            };
        }
        catch (ArgumentException ex) when (ex is not ArgumentNullException)
        {
            // Client-supplied arguments of any entry point. ArgumentNullException is
            // excluded: it is a contract check on injected services, i.e. a defect.
            return CreateInvalidParamsResponse(request.Id, ex);
        }
        catch (Exception ex) when (ToolErrorMapper.TryMap(ex, requestedPath: null, out var operational))
        {
            // Legacy direct RPC methods that do not render their own tool error, plus the
            // last-resort clause for tools/call (which handles its own mapped failures).
            // A fired deadline must be classified before the mapper, exactly as it is in
            // HandleToolsCallAsync and HandleLegacyToolCallAsync: the mapper sees only an
            // OperationCanceledException and cannot tell it apart from a peer cancel.
            if (ToolErrorMapper.IsCancellation(ex) && IsDeadlineExceeded(deadlineFired(), sessionEnded()))
            {
                return CreateToolErrorResponse(request.Id, ResourceLimitError());
            }

            return CreateErrorResponse(request.Id, LegacyOperationalErrorCode, operational.Code);
        }
        catch (Exception ex)
        {
            // An unexpected defect: the client gets a neutral message and a correlation
            // id, the full exception goes to the best-effort log and never to the wire.
            var correlationId = ToolErrorMapper.NewCorrelationId();
            // The logger is best effort by contract; the extra guard keeps the response
            // path independent even of a defect inside the logger itself.
            try { McpLogger.Error("Unhandled request failure.", ex, correlationId); } catch { }
            var isParseError = ex is JsonException;
            return CreateErrorResponse(
                id: request.Id,
                code: isParseError ? ParseErrorCode : InternalErrorCode,
                message: isParseError ? "Parse error" : "Internal error",
                correlationId: correlationId);
        }
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

    private static async Task<JsonRpcResponse> HandleToolsCallAsync(
        JsonRpcRequest request,
        ToolRegistry toolRegistry,
        ResourceLimiter limiter,
        CancellationToken cancellationToken,
        Func<bool> sessionEnded,
        Func<bool> deadlineFired)
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
            var toolResult = await toolRegistry.ExecuteToolAsync(
                parameters.Name, arguments, limiter, cancellationToken);
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
        catch (OperationCanceledException) when (IsDeadlineExceeded(deadlineFired(), sessionEnded()))
        {
            // FS-07: a fired operation deadline is a bounded-resource refusal, not a peer
            // cancellation. This clause must precede the mapper, because the mapper maps
            // every OperationCanceledException to `cancelled` from the exception type
            // alone and cannot tell the two causes apart.
            return CreateToolErrorResponse(request.Id, ResourceLimitError());
        }
        catch (Exception ex) when (ToolErrorMapper.TryMap(ex, requestedPath, out var error))
        {
            // Expected operational failure: result.isError with one serialized
            // {code,message,details?} object. Never built by string concatenation.
            return CreateToolErrorResponse(request.Id, error);
        }

        // Any other failure is an unexpected defect: it falls through to the single
        // -32603 correlation path, which logs it and never echoes it.
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
        try { McpLogger.Info("Invalid arguments: " + ToolErrorMapper.ArgumentFailureDetail(exception)); } catch { }
        return CreateErrorResponse(id, InvalidParamsCode, "Invalid arguments.");
    }

    /// <summary>
    /// The historical direct RPC methods reuse the registered tool implementation
    /// rather than a second, divergent code path, so their budgets, error codes and
    /// cancellation behaviour cannot drift away from <c>tools/call</c>. FS-12 removes
    /// them; until then the FS-01/FS-04 envelope (<c>-32001</c> with the code as the
    /// message) is preserved.
    /// </summary>
    private static async Task<JsonRpcResponse> HandleLegacyToolCallAsync(
        JsonRpcRequest request,
        string toolName,
        ToolRegistry toolRegistry,
        ResourceLimiter limiter,
        CancellationToken cancellationToken,
        Func<bool> sessionEnded,
        Func<bool> deadlineFired)
    {
        if (request.Params is not { } parameters || parameters.ValueKind is not JsonValueKind.Object)
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Missing or invalid " + toolName + " params.");
        }

        try
        {
            var toolResult = await toolRegistry.ExecuteToolAsync(toolName, parameters, limiter, cancellationToken);
            using var document = JsonDocument.Parse(toolResult);
            return CreateResultResponse(request.Id, document.RootElement.Clone());
        }
        catch (OperationCanceledException) when (IsDeadlineExceeded(deadlineFired(), sessionEnded()))
        {
            return CreateToolErrorResponse(request.Id, ResourceLimitError());
        }
        catch (Exception ex) when (ToolErrorMapper.TryMap(ex, requestedPath: null, out var error))
        {
            return CreateErrorResponse(request.Id, LegacyOperationalErrorCode, error.Code);
        }
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
