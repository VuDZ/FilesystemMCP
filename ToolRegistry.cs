using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ToolRegistry
{
    private readonly Dictionary<string, IMcpTool> _tools = new(StringComparer.Ordinal);
    private JsonElement _cachedToolsList;
    private bool _isToolsListCached;
    internal Action<string, TimeSpan, bool, string?> CompletionLog { get; set; } =
        (name, elapsed, success, detail) => McpLogger.LogToolComplete(name, elapsed, success, detail);

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

    public async Task<string> ExecuteToolAsync(string name, JsonElement arguments)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            // Typed, so the transport maps an unknown tool to -32602 instead of
            // guessing from the message text or reporting an internal error.
            throw new UnknownToolException(name);
        }

        try { McpLogger.LogToolInvoke(name, arguments); } catch { }
        var startedAt = Environment.TickCount64;

        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
            try { CompletionLog(name, elapsed, true, $"resultChars={result.Length}"); } catch { }
            return result;
        }
        catch (Exception ex)
        {
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
            // Log the machine code, never the exception message: a platform message can
            // embed an absolute path, while the code is the stable operation outcome.
            try { CompletionLog(name, elapsed, false, ToolErrorMapper.Map(ex)?.Code ?? "unexpected_error"); } catch { }
            throw;
        }
    }
}
