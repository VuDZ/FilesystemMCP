using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ToolRegistry
{
    private readonly Dictionary<string, IMcpTool> _tools = new(StringComparer.Ordinal);
    private JsonElement _cachedToolsList;
    private bool _isToolsListCached;

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

    public Task<string> ExecuteToolAsync(string name, JsonElement arguments)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            throw new InvalidOperationException("Tool not found");
        }

        return tool.ExecuteAsync(arguments);
    }
}
