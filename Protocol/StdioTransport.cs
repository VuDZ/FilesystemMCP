using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace FilesystemMcp;

/// <summary>
/// The stdio transport. It owns frame reading, request dispatch, output framing and
/// the FS-07 responsiveness contract:
/// <list type="bullet">
/// <item>a dedicated reader keeps accepting frames while a tool runs, so <c>ping</c>
/// and <c>notifications/cancelled</c> are never queued behind a tool;</item>
/// <item>one writer lock serializes whole frames, so concurrent tool results and
/// pings can never interleave inside a line or produce invalid JSON;</item>
/// <item>every response is budgeted before it reaches the wire: an oversized payload
/// becomes a bounded <c>resource_limit</c> object instead of truncated JSON;</item>
/// <item>each tool invocation runs under a linked token — session shutdown (EOF or a
/// dead output pipe) plus its own deadline — and is registered by request id so the
/// peer can cancel exactly that request.</item>
/// </list>
/// </summary>
internal sealed class StdioTransport
{
    private const int InvalidRequestCode = -32600;
    private const int InternalErrorCode = -32603;
    private const int ParseErrorCode = -32700;
    private const string CancelledNotification = "notifications/cancelled";
    private const string LegacyCancelledNotification = "$/cancelRequest";
    private const string ResponseBudgetMessage = "Response exceeds maxResponseChars";

    /// <summary>True once EOF or shutdown has cancelled the whole session.</summary>
    internal bool IsSessionCancelled
    {
        get
        {
            try
            {
                return _session.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// The transport is constructed with the validated budget only: an unvalidated or
    /// default-only transport could not be used at all, so there is no parameterless
    /// construction that silently bypasses the startup checks.
    /// </summary>
    public StdioTransport(ResourceBudget budget)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    /// <summary>Set by <see cref="Program"/> once the services exist.</summary>
    internal void Configure(RequestHandler handler, Func<bool> isClientInitialized)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _isClientInitialized = isClientInitialized ?? throw new ArgumentNullException(nameof(isClientInitialized));
    }

    /// <summary>
    /// Runs until stdin ends. Returns true when the session ended because the peer
    /// closed the stream, false when the output pipe died first; both are a normal
    /// shutdown, never a startup or protocol failure.
    /// </summary>
    internal async Task<bool> RunAsync()
    {
        var reader = Task.Run(ReadFramesAsync);
        var outcome = true;
        try
        {
            await foreach (var work in _frames.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (work.Frame is { } frame)
                {
                    await DispatchAsync(frame).ConfigureAwait(false);
                }
                else if (work.Malformed)
                {
                    WriteFrame(CreateErrorResponse(null, ParseErrorCode, "Parse error"));
                }
                else
                {
                    // A frame the bounded reader rejected: report it, execute nothing,
                    // and keep the stream aligned for the next frame (FS-07).
                    WriteFrame(CreateErrorResponse(null, InvalidRequestCode, "Invalid Request"));
                }
            }
        }
        catch (IOException)
        {
            // The output pipe died. Unfinished work is cancelled below; the process
            // still exits through the normal path instead of crashing.
            outcome = false;
        }
        finally
        {
            // The reader is awaited, not abandoned: an exception inside it (a broken stdin
            // handle, a defect in the frame reader) must not be swallowed while the session
            // reports a clean EOF and the process exits 0 as if nothing happened.
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = false;
                try
                {
                    McpLogger.Error("Frame reader failed.", ex);
                }
                catch
                {
                }
            }

            await DisposeSessionAsync().ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task DispatchAsync(string frame)
    {
        // A blank line is not silence. It is not JSON, so the decoder answers
        // -32700 with an explicit null id. Dropping it here made the next request
        // the first frame the client saw.
        var decoded = JsonRpcFrameDecoder.Decode(frame);
        if (decoded.Failure is { } failure)
        {
            WriteFrame(failure);
            return;
        }

        var request = decoded.Request;
        if (request is null)
        {
            WriteFrame(CreateErrorResponse(null, ParseErrorCode, "Parse error"));
            return;
        }

        // Cancellation is a notification. A request that happens to use the same method
        // name still gets exactly one response from the handler.
        if (!request.HasId && IsCancellation(request))
        {
            CancelInFlight(request.Params);
            return;
        }

        // initialize and every notification update session state. The reader does not
        // dispatch the next frame until that update is visible, so a pipelined
        // tools/list cannot overtake notifications/initialized. Neither path waits on a tool.
        if (!request.HasId || string.Equals(request.Method, "initialize", StringComparison.Ordinal))
        {
            await CompleteAsync(_handler(request, CancellationToken.None)).ConfigureAwait(false);
            return;
        }

        if (IsImmediate(request))
        {
            // ping and the list methods never wait behind a running tool.
            // Their session check runs on this reader, before the first await.
            Discard(CompleteAsync(_handler(request, CancellationToken.None)));
            return;
        }

        // tools/call and every other method are otherwise queued. Reading the
        // flag here, before the next frame, stops a pipelined
        // notifications/initialized from opening the session first and letting
        // the tool run. The handler repeats the same check.
        if (_isClientInitialized is null)
        {
            throw new InvalidOperationException("Transport was not configured.");
        }

        if (!_isClientInitialized())
        {
            await CompleteAsync(_handler(request, CancellationToken.None)).ConfigureAwait(false);
            return;
        }

        var key = RequestKey(request.Id);
        var lifetime = CreateRequestLifetime();
        // The lifetime is registered before the handler is entered, so a
        // notifications/cancelled frame that arrives while the request is still
        // queued is not lost.
        _inFlight[key] = lifetime;
        var tracked = TrackAsync(request, key, lifetime);
        _outstanding[tracked] = 0;
        foreach (var finished in _outstanding.Keys)
        {
            if (finished.IsCompleted)
            {
                _outstanding.TryRemove(finished, out _);
            }
        }
    }

    private bool IsImmediate(JsonRpcRequest request) => request.Method switch
    {
        "ping" or "initialize" or "initialized" or "notifications/initialized"
            or "tools/list" or "prompts/list" or "resources/list" => true,
        _ => false
    };

    private static bool IsCancellation(JsonRpcRequest request) =>
        string.Equals(request.Method, CancelledNotification, StringComparison.Ordinal)
        || string.Equals(request.Method, LegacyCancelledNotification, StringComparison.Ordinal);

    /// <summary>
    /// A request-scoped token covering session shutdown (EOF or a dead output pipe).
    /// The operation deadline is layered on top by the request handler, which is the
    /// only place that knows whether a cancelled request should become a partial
    /// search result, a <c>resource_limit</c> or a clean <c>cancelled</c> outcome.
    /// The deadline lives in a token, so it interrupts a tool parked on I/O; a
    /// watchdog timer is never used to synchronize anything.
    /// </summary>
    private CancellationTokenSource CreateRequestLifetime() =>
        CancellationTokenSource.CreateLinkedTokenSource(_session.Token);

    private async Task TrackAsync(JsonRpcRequest request, string key, CancellationTokenSource lifetime)
    {
        // The whole request runs on the thread pool: dispatch must return to the reader
        // loop immediately, or a long tool would delay ping and cancel frames again.
        var work = Task.Run(() => _handler(request, lifetime.Token));
        try
        {
            await CompleteAsync(work).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
            try
            {
                lifetime.Dispose();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Writes the reply for a completed request. It is deliberately not <c>async void</c>:
    /// every path returns a task the caller observes, so a defect here can never turn
    /// into an unhandled exception on the finalizer thread.
    /// </summary>
    private async Task CompleteAsync(Task<JsonRpcResponse?> work)
    {
        try
        {
            var response = await work.ConfigureAwait(false);
            // EOF and shutdown cancel the session before unfinished work is joined.
            // A tool that then reports cancelled has no peer left (FS-07 R10), so
            // the frame is not written. Peer notifications/cancelled leaves the
            // session alive, and that cancelled result is still delivered.
            if (response is not null && !IsSessionCancelled)
            {
                WriteFrame(response);
            }
        }
        catch (IOException)
        {
            // The peer is gone: shutdown cancels the rest.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already disposed an output owned by the host.
        }
    }

    /// <summary>Fire-and-forget for the immediate paths: the failure modes are already handled inside.</summary>
    private static void Discard(Task work) => _ = work;

    private static string RequestKey(JsonElement? id) =>
        id is { } value && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined
            ? value.GetRawText()
            : string.Empty;

    private void CancelInFlight(JsonElement? parameters)
    {
        if (parameters is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!value.TryGetProperty("requestId", out var requestId))
        {
            return;
        }

        if (_inFlight.TryGetValue(RequestKey(requestId), out var lifetime))
        {
            try
            {
                lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already finished
            }
        }
    }

    /// <summary>
    /// Bounded output, as one deterministic decision. The frame is measured; if it fits, it is
    /// sent. If it does not, it is replaced by the refusal for its kind and the replacement is
    /// measured again — and if even that cannot fit, by the minimal legal frame. Nothing is
    /// ever truncated: a cut frame is unparseable, which is worse than any refusal. Every
    /// branch returns valid JSON carrying the request id, and every branch that replaced a
    /// result carries the truncation cause when the replaced payload had one — including the
    /// minimal frame, where it travels in <c>error.data</c>.
    /// </summary>
    private void WriteFrame(JsonRpcResponse response)
    {
        var json = JsonSerializer.Serialize(response, McpJsonContext.Default.JsonRpcResponse);
        if (json.Length > _budget.MaxResponseChars)
        {
            var reason = ExtractTruncationReason(response);
            json = JsonSerializer.Serialize(
                CreateOversizedResponse(response.Id, response.Result, reason), McpJsonContext.Default.JsonRpcResponse);
            if (json.Length > _budget.MaxResponseChars)
            {
                json = JsonSerializer.Serialize(
                    CreateMinimalResponse(response.Id, reason), McpJsonContext.Default.JsonRpcResponse);
            }
        }

        lock (_writeLock)
        {
            var stdout = Console.Out;
            stdout.Write(json);
            stdout.Write('\n');
            stdout.Flush();
        }
    }

    /// <summary>
    /// The refusal that replaces a result which does not fit the response budget. A tool
    /// result keeps its <c>content</c>/<c>isError</c> envelope so the client still sees a
    /// tool error; every other over-budget result (a large <c>tools/list</c>, an error
    /// whose message is huge) becomes a bounded JSON-RPC error that preserves the id and
    /// reports the budget rather than pretending the server broke.
    /// </summary>
    private static JsonRpcResponse CreateOversizedResponse(JsonElement? id, JsonElement? result, string? reason)
    {
        if (result is not { } value || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("content", out _))
        {
            return CreateErrorResponse(id, InternalErrorCode, ResponseBudgetMessage, reason);
        }

        var error = ToolErrorMapper.Map(new ResourceLimitException(
            ToolErrorMessages.ForCode(ToolErrorCodes.ResourceLimit) + " (payload exceeds maxResponseChars)"))!;

        // The frame may have blown the budget purely through JSON escaping, after the tool
        // had honestly produced a bounded partial result. In that case the cause of the
        // truncation must survive: the client must not be told "resource_limit" where the
        // truth is "the search was already cut short by the deadline / traversal cap".
        if (reason is not null)
        {
            error = ToolErrorMapper.WithTruncationReason(error, reason);
        }

        var text = JsonSerializer.Serialize(error, McpJsonContext.Default.ToolOperationError);
        var bounded = new ToolsCallResult([new ToolCallContent("text", text)], IsError: true);
        return new JsonRpcResponse(
            JsonRpcConstants.Version,
            id,
            JsonSerializer.SerializeToElement(bounded, McpJsonContext.Default.ToolsCallResult),
            null);
    }

    /// <summary>
    /// Reads <c>truncation_reason</c> out of a tool payload that is about to be replaced. The
    /// payload is the tool's own JSON text inside <c>content[0].text</c>; anything that is not
    /// that shape simply has no reason to preserve. The value is accepted only when it is one
    /// of the tokens this server actually emits, so a tool can never smuggle arbitrary text
    /// into client-visible metadata.
    /// </summary>
    private static string? ExtractTruncationReason(JsonRpcResponse response)
    {
        if (response.Result is not { } result
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() == 0
            || !content[0].TryGetProperty("text", out var textNode)
            || textNode.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = textNode.GetString();
        if (string.IsNullOrEmpty(text) || text[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("truncation_reason", out var reason)
                && reason.ValueKind == JsonValueKind.String)
            {
                // Only tokens this server emits are accepted, so a tool cannot smuggle
                // arbitrary text into client-visible metadata.
                var value = reason.GetString()!;
                return _knownTruncationReasons.Contains(value) ? value : null;
            }
        }
        catch (JsonException)
        {
            // Not JSON: there is no reason to carry over.
        }

        return null;
    }

    /// <summary>
    /// The floor: the shortest refusal this transport will emit, used when the budget cannot
    /// hold even a full refusal. It stays a well-formed JSON-RPC error with the right id,
    /// because a frame that is not parseable is worse than one over budget, and it carries the
    /// truncation cause in <c>error.data</c> when there was one.
    /// </summary>
    private static JsonRpcResponse CreateMinimalResponse(JsonElement? id, string? reason) =>
        CreateErrorResponse(id, InternalErrorCode, ResponseBudgetMessage, reason);

    private async Task ReadFramesAsync()
    {
        try
        {
            // Constructed inside the guard: a failure here must still complete the channel,
            // or the session would wait forever for frames that can never arrive.
            var reader = new BoundedFrameReader(Console.OpenStandardInput(), _budget.MaxRequestBytes);
            while (true)
            {
                var result = await reader.ReadFrameAsync(_session.Token).ConfigureAwait(false);
                if (result.EndOfStream)
                {
                    break;
                }

                var work = result.IsOversized ? FrameWork.Rejected
                    : result.IsMalformed ? FrameWork.Unreadable
                    : FrameWork.Of(result.Text!);
                if (!_frames.Writer.TryWrite(work))
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A dead input pipe is EOF for this transport.
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    /// <summary>
    /// EOF and shutdown cancel every unfinished operation. A mutation that has not
    /// crossed its commit point (FS-02) observes the token and leaves the original
    /// untouched; a committed one is already durable.
    /// </summary>
    private async Task DisposeSessionAsync()
    {
        try
        {
            _session.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (var task in _outstanding.Keys.ToArray())
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // already reported, or the transport is gone
            }
        }

        try
        {
            _session.Dispose();
        }
        catch
        {
        }
    }

    private static JsonRpcResponse CreateErrorResponse(JsonElement? id, int code, string message, string? truncationReason = null) =>
        new(JsonRpcConstants.Version, JsonRpcIds.OrNull(id), null, new JsonRpcError(
            code,
            message,
            truncationReason is null
                ? null
                : JsonSerializer.SerializeToElement(
                    new TruncationReasonData(truncationReason), McpJsonContext.Default.TruncationReasonData)));

    /// <summary>Handles one decoded request and produces its reply, or null for a notification.</summary>
    internal delegate Task<JsonRpcResponse?> RequestHandler(JsonRpcRequest? request, CancellationToken cancellationToken);

    private readonly record struct FrameWork(string? Frame, bool Malformed)
    {
        public static FrameWork Rejected { get; } = new((string?)null, false);
        public static FrameWork Unreadable { get; } = new((string?)null, true);
        public static FrameWork Of(string frame) => new(frame, false);
    }

    private readonly ResourceBudget _budget;
    private readonly CancellationTokenSource _session = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _outstanding = new();
    private readonly Channel<FrameWork> _frames = Channel.CreateUnbounded<FrameWork>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });
    private readonly object _writeLock = new();
    private RequestHandler _handler = null!;
    private Func<bool> _isClientInitialized = null!;

    /// <summary>
    /// The closed set of truncation causes the transport will forward into client-visible
    /// metadata. It mirrors the FS-07 contract table; a value outside it is dropped rather
    /// than passed through, so this list is the enforcement point for that table.
    /// </summary>
    private static readonly HashSet<string> _knownTruncationReasons = new(StringComparer.Ordinal)
    {
        "search_max_files",
        "search_max_directories",
        "max_response_chars",
        "operation_timeout"
    };
}
