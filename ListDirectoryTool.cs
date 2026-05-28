using System.Buffers;
using System.Text;
using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ListDirectoryTool : IMcpTool
{
    private const string Schema = """
{
  "type": "object",
  "additionalProperties": false,
  "required": ["path"],
  "properties": {
    "path": { "type": "string", "minLength": 1 }
  }
}
""";

    private readonly string _workspaceRoot;

    public ListDirectoryTool(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("WorkspaceRoot must be provided.", nameof(workspaceRoot));
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public string Name => "list_directory";
    public string Description => "Lists files and folders in a directory. ALWAYS use this to explore the project structure before assuming file paths.";
    public string InputSchemaJson => Schema;

    public Task<string> ExecuteAsync(JsonElement arguments)
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

        var path = pathNode.GetString();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Argument path cannot be empty.");
        }

        var resolved = WorkspaceJail.ResolvePath(_workspaceRoot, path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException("Directory not found.");
        }

        var directories = Directory.GetDirectories(resolved);
        var files = Directory.GetFiles(resolved);

        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var directory in directories.OrderBy(static d => d, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("name", Path.GetFileName(directory));
                writer.WriteString("type", "directory");
                writer.WriteEndObject();
            }

            foreach (var file in files.OrderBy(static f => f, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("name", Path.GetFileName(file));
                writer.WriteString("type", "file");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Task.FromResult(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
