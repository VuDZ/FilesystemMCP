using System.Text.Json;

namespace FilesystemMcp;

internal sealed class CreateFileTool : IMcpTool
{
    private const string Schema = """
{
  "type": "object",
  "additionalProperties": false,
  "anyOf": [
    { "required": ["path"] },
    { "required": ["filePath"] },
    { "required": ["file_path"] }
  ],
  "required": ["content"],
  "properties": {
    "path": { "type": "string", "minLength": 1, "pattern": "\\S" },
    "filePath": { "type": "string", "minLength": 1, "pattern": "\\S", "description": "Alias for path." },
    "file_path": { "type": "string", "minLength": 1, "pattern": "\\S", "description": "Alias for path." },
    "content": { "type": "string" }
  }
}
""";

    private readonly FileOperationsService _operations;

    public CreateFileTool(string workspaceRoot) : this(new FileOperationsService(workspaceRoot)) { }

    public CreateFileTool(PathPolicy policy) : this(new FileOperationsService(policy)) { }

    public CreateFileTool(FileOperationsService operations) =>
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));

    public string Name => "create_file";
    public string Description =>
        "Creates a strictly NEW file. Do NOT use this to edit existing files (use replace_in_file instead). "
        + "Path argument: path, filePath, or file_path (at least one; when more than one is set they must be equal).";
    public string InputSchemaJson => Schema;

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ToolArguments.RejectUnknownProperties(arguments, "path", "filePath", "file_path", "content");
        var path = ToolArguments.GetRequiredPath(arguments);

        if (!arguments.TryGetProperty("content", out var contentNode)
            || contentNode.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || contentNode.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Missing required argument: content.");
        }

        var content = contentNode.GetString() ?? string.Empty;
        var result = await _operations.CreateFileAsync(path, content, cancellationToken);
        return JsonSerializer.Serialize(
            new CreateFileToolResult("success", result.Path, result.Md5, result.Sha256),
            McpJsonContext.Default.CreateFileToolResult);
    }
}
