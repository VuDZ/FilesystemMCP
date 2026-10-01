using System.Text;
using System.Text.Json;

namespace FilesystemMcp;

internal sealed class CreateFileTool : IMcpTool
{
    private const string Schema = """
{
  "type": "object",
  "additionalProperties": false,
  "required": ["content"],
  "properties": {
    "path": { "type": "string", "minLength": 1 },
    "filePath": { "type": "string", "minLength": 1, "description": "Alias for path." },
    "file_path": { "type": "string", "minLength": 1, "description": "Alias for path." },
    "content": { "type": "string" }
  }
}
""";

    private readonly PathPolicy _policy;
    private string _workspaceRoot => _policy.Root;

    public CreateFileTool(string workspaceRoot) : this(new PathPolicy(workspaceRoot)) { }

    public CreateFileTool(PathPolicy policy) => _policy = policy;

    public string Name => "create_file";
    public string Description =>
        "Creates a strictly NEW file. Do NOT use this to edit existing files (use replace_in_file instead). "
        + "Path argument: path, filePath, or file_path (one required).";
    public string InputSchemaJson => Schema;

    public async Task<string> ExecuteAsync(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        var path = ToolArguments.GetRequiredPath(arguments);

        if (!arguments.TryGetProperty("content", out var contentNode)
            || contentNode.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || contentNode.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Missing required argument: content.");
        }

        var content = contentNode.GetString() ?? string.Empty;
        var resolved = _policy.Resolve(path);
        if (File.Exists(resolved))
        {
            throw new InvalidOperationException("File already exists. Use replace_in_file.");
        }

        await NativePath.WriteAsync(_policy, path, resolved, content, true);

        return "{\"status\":\"success\"}";
    }
}
