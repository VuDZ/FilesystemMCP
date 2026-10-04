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
            if (AtomicWritesForHost is not null)
            {
                policy.AtomicWrites = AtomicWritesForHost;
            }
            // FS-07: the read-parallelism budget is part of startup validation, so an
            // impossible value can never reach the dispatcher.
            limiter = new ResourceLimiter(options.Budget.MaxConcurrentReads);
            // FS-06: diagnostics are best effort from the first record on. A logging
            // failure here can never fail startup, and an unavailable directory only
            // disables the file sink.
            try
            {
                McpLogger.Start(options.LogDirectory);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Startup failed: " + (ex is PathPolicyException pathError ? pathError.Code : ex.Message));
            return 1;
        }

        var operations = new FileOperationsService(policy) { Budget = options.Budget };
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new ListDirectoryTool(policy, options.Budget));
        toolRegistry.Register(new SearchTool(policy, options.Budget));
        toolRegistry.Register(new CreateFileTool(operations));
        toolRegistry.Register(new ReadFileTool(operations));
        toolRegistry.Register(new ReplaceInFileTool(operations));

        // FS-07: one reader, one writer lock. Tools execute off the read path, so ping
        // and notifications/cancelled stay responsive while a tool is running.
        // FS-13: initialize is accepted once; tools stay closed until notifications/initialized.
        var session = new ProtocolSession();
        var transport = new StdioTransport(options.Budget);
        transport.Configure(
            (request, cancellationToken) => HandleAsync(
                request, toolRegistry, limiter, options, session, cancellationToken),
            () => session.IsClientInitialized);
        try
        {
            await transport.RunAsync();
            return 0;
        }
        finally
        {
            // FS-06: bounded exit flush. Diagnostics are not allowed to delay shutdown,
            // and whatever is already queued is written before the process leaves.
            try
            {
                McpLogger.Shutdown(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
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
        ToolRegistry toolRegistry,
        ResourceLimiter limiter,
        ServerOptions options,
        ProtocolSession session,
        CancellationToken sessionToken)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (request is null)
        {
            return CreateErrorResponse(null, ParseErrorCode, "Parse error");
        }

        // Notifications are a separate branch from errors whose id is null. A missing id
        // member is a notification and is never answered, including unknown methods.
        // Parse errors and invalid ids still produce a response; those are written by the
        // transport before this method runs, with an explicit JSON null id.
        if (!request.HasId)
        {
            if (IsInitializedNotification(request.Method))
            {
                session.MarkClientInitialized();
            }

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
            try
            {
                deadline.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
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
            return await ProcessRequestAsync(request, toolRegistry, limiter, session, () => sessionEnded, () => deadlineFired, deadline.Token);
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
        ToolRegistry toolRegistry,
        ResourceLimiter limiter,
        ProtocolSession session,
        Func<bool> sessionEnded,
        Func<bool> deadlineFired,
        CancellationToken cancellationToken)
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

        // A notification method that arrived with an id is a request, so it must be
        // answered. It is not a lifecycle failure and it is not an unknown method.
        if (IsNotificationMethod(request.Method))
        {
            return CreateErrorResponse(
                request.Id,
                InvalidRequestCode,
                "Method " + request.Method + " is a notification and must not include an id.");
        }

        // Before notifications/initialized the only requests are initialize and ping.
        // Tools and every other method are rejected without running.
        if (!session.IsClientInitialized && request.Method is not ("initialize" or "ping"))
        {
            return CreateErrorResponse(
                request.Id,
                InvalidRequestCode,
                "Server is not initialized. Send notifications/initialized before calling " + request.Method + ".");
        }

        if (!IsKnownRequestMethod(request.Method))
        {
            // Unknown method wins over a bad params shape: there is no schema to validate.
            return CreateErrorResponse(request.Id, -32601, "Method not found: " + request.Method);
        }

        if (TryRejectNonObjectParams(request, out var paramsError))
        {
            return paramsError;
        }

        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request, session),
                "ping" => HandlePing(request.Id),
                "tools/list" => HandleToolsList(toolRegistry, request.Id),
                "tools/call" => await HandleToolsCallAsync(request, toolRegistry, limiter, sessionEnded, deadlineFired, cancellationToken),
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
        catch (JsonException)
        {
            // A params object that cannot be bound is an invalid-params failure.
            // The frame was already parsed; this is not a -32700 syntax error (FS-13).
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Invalid params.");
        }
        catch (Exception ex) when (ToolErrorMapper.TryMap(ex, requestedPath: null, out var operational))
        {
            // Last resort for a mapped failure that escaped a tool. Direct RPC methods are
            // gone (FS-12): there is no -32001 envelope. A fired deadline is a resource
            // limit, not a peer cancel — the mapper cannot tell those apart.
            if (ToolErrorMapper.IsCancellation(ex) && IsDeadlineExceeded(deadlineFired(), sessionEnded()))
            {
                return CreateToolErrorResponse(request.Id, ResourceLimitError());
            }

            return CreateToolErrorResponse(request.Id, operational);
        }
        catch (Exception ex)
        {
            // An unexpected defect: the client gets a neutral message and a correlation
            // id, the full exception goes to the best-effort log and never to the wire.
            var correlationId = ToolErrorMapper.NewCorrelationId();
            // The logger is best effort by contract; the extra guard keeps the response
            // path independent even of a defect inside the logger itself.
            try
            {
                McpLogger.Error("Unhandled request failure.", ex, correlationId);
            }
            catch
            {
            }
            return CreateErrorResponse(
                id: request.Id,
                code: InternalErrorCode,
                message: "Internal error",
                correlationId: correlationId);
        }
    }

    private static JsonRpcResponse HandleInitialize(JsonRpcRequest request, ProtocolSession session)
    {
        if (!request.HasParams || request.Params is not { } parameters || parameters.ValueKind != JsonValueKind.Object)
        {
            return CreateErrorResponse(request.Id, InvalidParamsCode, "Invalid params: params must be a JSON object.");
        }

        if (!TryGetRequiredString(parameters, "protocolVersion", nonEmpty: true, out var protocolVersion))
        {
            return CreateErrorResponse(
                request.Id,
                InvalidParamsCode,
                "Invalid params: protocolVersion is required and must be a non-empty string.");
        }

        if (!parameters.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
        {
            return CreateErrorResponse(
                request.Id,
                InvalidParamsCode,
                "Invalid params: capabilities is required and must be an object.");
        }

        if (!parameters.TryGetProperty("clientInfo", out var clientInfo) || clientInfo.ValueKind != JsonValueKind.Object)
        {
            return CreateErrorResponse(
                request.Id,
                InvalidParamsCode,
                "Invalid params: clientInfo is required and must be an object.");
        }

        if (!TryGetRequiredString(clientInfo, "name", nonEmpty: false, out _))
        {
            return CreateErrorResponse(
                request.Id,
                InvalidParamsCode,
                "Invalid params: clientInfo.name is required and must be a string.");
        }

        if (!TryGetRequiredString(clientInfo, "version", nonEmpty: false, out _))
        {
            return CreateErrorResponse(
                request.Id,
                InvalidParamsCode,
                "Invalid params: clientInfo.version is required and must be a string.");
        }

        // Validation failures do not consume the one initialize. A second successful
        // initialize is an invalid request, not another handshake.
        if (!session.TryAcceptInitialize())
        {
            return CreateErrorResponse(request.Id, InvalidRequestCode, "Initialize was already completed.");
        }

        var negotiated = _supportedProtocolVersions.Contains(protocolVersion)
            ? protocolVersion
            : DefaultProtocolVersion;
        var serverInfo = new ServerInfo(ServerName, GetServerVersion());
        var result = new InitializeResult(negotiated, _serverCapabilities, serverInfo);
        var payload = JsonSerializer.SerializeToElement(result, McpJsonContext.Default.InitializeResult);
        return CreateResultResponse(request.Id, payload);
    }

    private static bool TryGetRequiredString(JsonElement owner, string name, bool nonEmpty, out string value)
    {
        if (!owner.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
        {
            value = string.Empty;
            return false;
        }

        value = node.GetString() ?? string.Empty;
        return !nonEmpty || !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsInitializedNotification(string method) =>
        method is "notifications/initialized" or "initialized";

    private static bool IsNotificationMethod(string method) =>
        method is "notifications/initialized" or "initialized" or "notifications/cancelled" or "$/cancelRequest";

    private static bool IsKnownRequestMethod(string method) =>
        method is "initialize" or "ping" or "tools/list" or "tools/call" or "prompts/list" or "resources/list";

    /// <summary>
    /// Current methods take a JSON object for <c>params</c>. A missing member is allowed
    /// and interpreted by the method; null, array and scalars are -32602, never -32700.
    /// </summary>
    private static bool TryRejectNonObjectParams(JsonRpcRequest request, out JsonRpcResponse? error)
    {
        if (!request.HasParams || request.Params is { } node && node.ValueKind == JsonValueKind.Object)
        {
            error = null;
            return false;
        }

        var message = string.Equals(request.Method, "tools/call", StringComparison.Ordinal)
            ? "Missing or invalid tools/call params."
            : "Invalid params: params must be a JSON object.";
        error = CreateErrorResponse(request.Id, InvalidParamsCode, message);
        return true;
    }

    private static JsonRpcResponse HandlePing(JsonElement? id) =>
        CreateResultResponse(id, EmptyObject());

    private static JsonRpcResponse HandleToolsList(ToolRegistry toolRegistry, JsonElement? id) =>
        CreateResultResponse(id, toolRegistry.GetToolsListAsJson());

    private static JsonRpcResponse HandlePromptsList(JsonElement? id) =>
        CreateResultResponse(id, _emptyPromptsList);

    private static JsonRpcResponse HandleResourcesList(JsonElement? id) =>
        CreateResultResponse(id, _emptyResourcesList);

    private static async Task<JsonRpcResponse> HandleToolsCallAsync(
        JsonRpcRequest request,
        ToolRegistry toolRegistry,
        ResourceLimiter limiter,
        Func<bool> sessionEnded,
        Func<bool> deadlineFired,
        CancellationToken cancellationToken)
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
        try
        {
            McpLogger.Info("Invalid arguments: " + ToolErrorMapper.ArgumentFailureDetail(exception));
        }
        catch
        {
        }
        return CreateErrorResponse(id, InvalidParamsCode, "Invalid arguments.");
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
        new(JsonRpcConstants.Version, JsonRpcIds.OrNull(id), null, new JsonRpcError(
            code,
            message,
            correlationId is null
                ? null
                : JsonSerializer.SerializeToElement(new ErrorCorrelationData(correlationId), McpJsonContext.Default.ErrorCorrelationData)));

    /// <summary>
    /// One process, one handshake. <see cref="TryAcceptInitialize"/> flips only after a
    /// valid initialize. <see cref="MarkClientInitialized"/> is ignored until that happens,
    /// so a premature <c>notifications/initialized</c> does not open the tools.
    /// </summary>
    private sealed class ProtocolSession
    {
        public bool IsClientInitialized => Volatile.Read(ref _clientInitialized) != 0;

        public bool TryAcceptInitialize() =>
            Interlocked.CompareExchange(ref _initializeAccepted, 1, 0) == 0;

        public void MarkClientInitialized()
        {
            if (Volatile.Read(ref _initializeAccepted) != 0)
            {
                Volatile.Write(ref _clientInitialized, 1);
            }
        }

        private int _initializeAccepted;
        private int _clientInitialized;
    }

    /// <summary>
    /// Versions this server implements. A requested member of this set is reflected;
    /// anything else is answered with <see cref="DefaultProtocolVersion"/>. Client text
    /// is never copied through just because it parsed.
    /// </summary>
    private static readonly HashSet<string> _supportedProtocolVersions = new(StringComparer.Ordinal)
    {
        DefaultProtocolVersion
    };
    private static readonly JsonElement _serverCapabilities = ParseJsonElement("""{"tools":{"listChanged":false}}""");
    private static readonly JsonElement _emptyPromptsList = ParseJsonElement("""{"prompts":[]}""");
    private static readonly JsonElement _emptyResourcesList = ParseJsonElement("""{"resources":[]}""");
}
