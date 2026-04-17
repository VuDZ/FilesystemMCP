using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ReplaceInFileTool : IMcpTool
{
    private const int MaxSnippetLength = 400;
    private const string Schema = """
{
  "type": "object",
  "additionalProperties": false,
  "required": ["path", "target_snippet", "replacement_snippet", "original_hash"],
  "properties": {
    "path": { "type": "string", "minLength": 1 },
    "target_snippet": { "type": "string", "minLength": 1 },
    "replacement_snippet": { "type": "string" },
    "original_hash": { "type": "string", "minLength": 1 }
  }
}
""";

    private readonly FileService _fileService;

    public ReplaceInFileTool(FileService fileService)
    {
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
    }

    public string Name => "replace_in_file";
    public string Description => "Replaces a specific snippet of code in a file. Requires exact string match and the file's current hash for optimistic locking.";
    public string InputSchemaJson => Schema;

    public async Task<string> ExecuteAsync(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        var path = GetRequiredString(arguments, "path");
        var targetSnippet = GetRequiredString(arguments, "target_snippet");
        var replacementSnippet = GetRequiredString(arguments, "replacement_snippet");
        var originalHash = GetRequiredString(arguments, "original_hash");

        var (newText, newHash) = await _fileService.ReplaceInFileAsync(
            path,
            targetSnippet,
            replacementSnippet,
            originalHash);

        var result = new ReplaceInFileToolResult(
            Status: "success",
            NewHash: newHash,
            Snippet: BuildSnippet(newText, replacementSnippet));

        return JsonSerializer.Serialize(result, McpJsonContext.Default.ReplaceInFileToolResult);
    }

    private static string GetRequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var node)
            || node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || node.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Missing required argument: {propertyName}.");
        }

        var value = node.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Argument {propertyName} cannot be empty.");
        }

        return value;
    }

    private static string BuildSnippet(string text, string replacementSnippet)
    {
        if (text.Length <= MaxSnippetLength)
        {
            return text;
        }

        var searchToken = replacementSnippet.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var sourceText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var index = sourceText.IndexOf(searchToken, StringComparison.Ordinal);
        if (index < 0)
        {
            return sourceText[..MaxSnippetLength];
        }

        var start = Math.Max(0, index - (MaxSnippetLength / 2));
        var length = Math.Min(MaxSnippetLength, sourceText.Length - start);
        return sourceText.Substring(start, length);
    }
}
