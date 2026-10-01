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
  "properties": {
    "path": { "type": "string", "minLength": 1 },
    "filePath": { "type": "string", "minLength": 1, "description": "Alias for path." },
    "file_path": { "type": "string", "minLength": 1, "description": "Alias for path." }
  }
}
""";

    private readonly PathPolicy _policy;
    private string _workspaceRoot => _policy.Root;

    public ListDirectoryTool(string workspaceRoot) : this(new PathPolicy(workspaceRoot)) { }

    public ListDirectoryTool(PathPolicy policy) => _policy = policy;

    public string Name => "list_directory";
    public string Description =>
        "Lists files and folders in a directory. Path argument: path, filePath, or file_path (one required). "
        + "ALWAYS use this to explore the project structure before assuming file paths.";
    public string InputSchemaJson => Schema;

    public Task<string> ExecuteAsync(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        var path = ToolArguments.GetRequiredPath(arguments);

        var resolved = _policy.Resolve(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException("Directory not found.");
        }

        var entries = Directory.EnumerateFileSystemEntries(resolved).OrderBy(static p => p, PathPolicy.Comparer);
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("entries");
            foreach (var entry in entries)
            {
                var attributes = File.GetAttributes(entry);
                writer.WriteStartObject();
                writer.WriteString("name", Path.GetFileName(entry));
                writer.WriteString("type", (attributes & FileAttributes.ReparsePoint) != 0 ? "link"
                    : (attributes & FileAttributes.Directory) != 0 ? "directory" : "file");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Task.FromResult(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
