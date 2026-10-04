using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ReadFileTool : IMcpTool
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
  "properties": {
    "path": { "type": "string", "minLength": 1, "pattern": "\\S" },
    "filePath": { "type": "string", "minLength": 1, "pattern": "\\S", "description": "Alias for path." },
    "file_path": { "type": "string", "minLength": 1, "pattern": "\\S", "description": "Alias for path." },
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

    public string Name => "read_file";
    public string Description =>
        "Reads a text file (UTF-8 with or without BOM; UTF-16/UTF-32 LE/BE with BOM). Invalid encodings are rejected. Returns text and md5/sha256 hashes of the full normalized file. "
        + "The text keeps leading blank lines and the terminal newline, and includes the line delimiter after the last returned line when it existed in the file. "
        + "Metadata: total_lines (whole file), start_line/end_line (the range actually returned, absent for an empty selection), truncated (a line cap fired), has_more (lines exist after end_line). "
        + "Path argument: path, filePath, or file_path (at least one; when more than one is set they must be equal). "
        + "Default full-file limit is 1000 lines; set allow_large_read=true to read more (optionally with max_lines, which requires allow_large_read=true). "
        + "Always use before replace_in_file.";
    public string InputSchemaJson => Schema;

    public ReadFileTool(FileOperationsService operations) =>
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ToolArguments.RejectUnknownProperties(
            arguments, "path", "filePath", "file_path", "start_line", "end_line", "allow_large_read", "max_lines");
        var path = ToolArguments.GetRequiredPath(arguments);

        var options = new ReadFileOptions(
            StartLine: ParseOptionalInt(arguments, "start_line"),
            EndLine: ParseOptionalInt(arguments, "end_line"),
            AllowLargeRead: ParseOptionalBool(arguments, "allow_large_read"),
            MaxLines: ParseOptionalInt(arguments, "max_lines"));

        var result = await _operations.ReadFileAsync(path, options, cancellationToken);
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

    private readonly FileOperationsService _operations;
}
