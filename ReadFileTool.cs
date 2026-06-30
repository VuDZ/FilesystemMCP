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
    "end_line": { "type": "integer", "minimum": 1 },
    "allow_large_read": {
      "type": "boolean",
      "default": false,
      "description": "Explicit opt-in to read more than 1000 lines in one request."
    },
    "max_lines": {
      "type": "integer",
      "minimum": 1,
      "maximum": 50000,
      "description": "Custom line limit when allow_large_read is true. Defaults to 50000."
    }
  }
}
""";

    private readonly FileService _fileService;

    public ReadFileTool(FileService fileService)
    {
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
    }

    public string Name => "read_file";
    public string Description =>
        "Reads a text file (UTF-8/UTF-16 with BOM). Returns text and md5/sha256 hashes of the full normalized file. "
        + "Default full-file limit is 1000 lines; set allow_large_read=true to read more (optionally with max_lines). "
        + "Always use before replace_in_file.";
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

        var options = new ReadFileOptions(
            StartLine: ParseOptionalInt(arguments, "start_line"),
            EndLine: ParseOptionalInt(arguments, "end_line"),
            AllowLargeRead: ParseOptionalBool(arguments, "allow_large_read"),
            MaxLines: ParseOptionalInt(arguments, "max_lines"));

        var result = await _fileService.ReadFileAsync(path, options);
        return JsonSerializer.Serialize(result, McpJsonContext.Default.ReadFileResult);
    }

    private static int? ParseOptionalInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Number || !node.TryGetInt32(out var value))
        {
            throw new ArgumentException($"{propertyName} must be an integer.");
        }

        return value;
    }

    private static bool ParseOptionalBool(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (node.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (node.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        throw new ArgumentException($"{propertyName} must be a boolean.");
    }
}
