using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ReadFileTool : IMcpTool
{
    private const string Schema = """
{
  "type": "object",
  "additionalProperties": false,
  "required": ["path"],
  "properties": {
    "path": { "type": "string", "minLength": 1 },
    "start_line": { "type": "integer", "minimum": 1 },
    "end_line": { "type": "integer", "minimum": 1 }
  }
}
""";

    private readonly FileService _fileService;

    public ReadFileTool(FileService fileService)
    {
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
    }

    public string Name => "read_file";
    public string Description => "Reads a file's content. Returns the text and a 'hash'. Always use this tool BEFORE patching a file to get the current state and the required 'hash' for replace_in_file. Binary files are rejected.";
    public string InputSchemaJson => Schema;

    public async Task<string> ExecuteAsync(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        if (!arguments.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Missing required argument: path.");
        }

        var path = pathNode.GetString();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Argument path cannot be empty.");
        }

        int? startLine = null;
        int? endLine = null;

        if (arguments.TryGetProperty("start_line", out var startNode) && startNode.ValueKind != JsonValueKind.Null)
        {
            if (startNode.ValueKind != JsonValueKind.Number || !startNode.TryGetInt32(out var startValue))
            {
                throw new ArgumentException("start_line must be an integer.");
            }

            startLine = startValue;
        }

        if (arguments.TryGetProperty("end_line", out var endNode) && endNode.ValueKind != JsonValueKind.Null)
        {
            if (endNode.ValueKind != JsonValueKind.Number || !endNode.TryGetInt32(out var endValue))
            {
                throw new ArgumentException("end_line must be an integer.");
            }

            endLine = endValue;
        }

        var result = await _fileService.ReadFileAsync(path, startLine, endLine);
        return JsonSerializer.Serialize(result, McpJsonContext.Default.ReadFileResult);
    }
}
