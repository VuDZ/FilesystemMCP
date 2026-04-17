using System.Text.Json;
using System.Text;

namespace FilesystemMcp;

internal sealed class CreateFileTool : IMcpTool
{
    private const string Schema = """
{
  "type": "object",
  "additionalProperties": false,
  "required": ["path", "content"],
  "properties": {
    "path": { "type": "string", "minLength": 1 },
    "content": { "type": "string" }
  }
}
""";

    private readonly string _workspaceRoot;

    public CreateFileTool(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("WorkspaceRoot must be provided.", nameof(workspaceRoot));
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public string Name => "create_file";
    public string Description => "Creates a strictly NEW file. Do NOT use this to edit existing files (use replace_in_file instead).";
    public string InputSchemaJson => Schema;

    public async Task<string> ExecuteAsync(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        if (!arguments.TryGetProperty("path", out var pathNode)
            || pathNode.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || pathNode.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Missing required argument: path.");
        }

        if (!arguments.TryGetProperty("content", out var contentNode)
            || contentNode.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || contentNode.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Missing required argument: content.");
        }

        var path = pathNode.GetString();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Argument path cannot be empty.");
        }

        var content = contentNode.GetString() ?? string.Empty;
        var resolved = WorkspaceJail.ResolvePath(_workspaceRoot, path);
        if (File.Exists(resolved))
        {
            throw new InvalidOperationException("File already exists. Use replace_in_file.");
        }

        var directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var stream = new FileStream(
                         resolved,
                         new FileStreamOptions
                         {
                             Access = FileAccess.Write,
                             Mode = FileMode.CreateNew,
                             Share = FileShare.ReadWrite,
                             Options = FileOptions.SequentialScan
                         }))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(content.AsMemory());
            await writer.FlushAsync();
        }

        return "{\"status\":\"success\"}";
    }
}
