using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ToolRegistry
{
    internal Action<string, TimeSpan, bool, string?> CompletionLog
    {
        get; set;
    } =
        (name, elapsed, success, detail) => McpLogger.ToolComplete(name, elapsed, success, detail);

    public void Register(IMcpTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException("Tool name cannot be empty.", nameof(tool));
        }

        _tools[tool.Name] = tool;
        _isToolsListCached = false;
    }

    public JsonElement GetToolsListAsJson()
    {
        if (_isToolsListCached)
        {
            return _cachedToolsList;
        }

        var list = new List<ToolDefinition>(_tools.Count);
        foreach (var tool in _tools.Values)
        {
            using var schemaDocument = JsonDocument.Parse(tool.InputSchemaJson);
            var schemaElement = schemaDocument.RootElement.Clone();
            list.Add(new ToolDefinition(tool.Name, tool.Description, schemaElement));
        }

        var payload = new ToolsListResult(list);
        _cachedToolsList = JsonSerializer.SerializeToElement(payload, McpJsonContext.Default.ToolsListResult);
        _isToolsListCached = true;
        return _cachedToolsList;
    }

    /// <summary>
    /// Runs one tool invocation. <paramref name="limiter"/> may be null only when the
    /// caller does not want the read-parallelism budget at all (a mutation, or an
    /// in-process unit test); the dispatcher always passes the real one.
    /// </summary>
    public async Task<string> ExecuteToolAsync(
        string name,
        JsonElement arguments,
        ResourceLimiter? limiter = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            // Typed, so the transport maps an unknown tool to -32602 instead of
            // guessing from the message text or reporting an internal error.
            throw new UnknownToolException(name);
        }

        try
        {
            McpLogger.ToolInvoke(name, arguments);
        }
        catch
        {
        }
        var startedAt = Environment.TickCount64;
        var useLimiter = limiter is not null && _readOnlyTools.Contains(name);

        try
        {
            var result = useLimiter
                ? await limiter!.RunAsync(token => InvokeAsync(tool, arguments, token), cancellationToken)
                : await InvokeAsync(tool, arguments, cancellationToken);
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
            try
            {
                CompletionLog(name, elapsed, true, $"resultChars={result.Length}");
            }
            catch
            {
            }
            return result;
        }
        catch (Exception ex)
        {
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
            // Log the machine code, never the exception message: a platform message can
            // embed an absolute path, while the code is the stable operation outcome.
            try
            {
                CompletionLog(name, elapsed, false, ToolErrorMapper.Map(ex)?.Code ?? "unexpected_error");
            }
            catch
            {
            }
            throw;
        }
    }

    /// <summary>
    /// One tool invocation inside its concurrency slot. The deterministic test hook
    /// runs here — after the slot is held — so a stdio barrier proves the real
    /// parallelism budget instead of merely pausing a request. The operation deadline is
    /// published here, where the tool body actually runs, so a tool that can return a
    /// partial result can tell a fired deadline from a peer cancel by fact rather than
    /// by guessing from elapsed wall-clock time.
    /// </summary>
    private static async Task<string> InvokeAsync(IMcpTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        // Re-published from the request scope, never derived from the tool's own token:
        // that token is cancelled by a peer cancel as well, so it cannot express the cause
        // and using it made every peer cancel look like a fired deadline.
        using var deadline = RequestDeadline.Begin(RequestDeadline.Token ?? cancellationToken);
        TestHooks.ReachToolExecution(tool.Name);
        return await tool.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tools that read the workspace share the FS-07 <c>maxConcurrentReads</c> budget.
    /// Mutations are deliberately absent: FS-02 already serializes them by file
    /// identity, and a queued writer must not consume read capacity.
    /// </summary>
    private static readonly HashSet<string> _readOnlyTools = new(StringComparer.Ordinal)
    {
        "read_file",
        "search",
        "list_directory"
    };

    private readonly Dictionary<string, IMcpTool> _tools = new(StringComparer.Ordinal);
    private JsonElement _cachedToolsList;
    private bool _isToolsListCached;
}
