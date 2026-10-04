using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ReplaceInFileTool : IMcpTool
{
    private const int MaxSnippetLength = 400;
    private const string Schema = """
{
  "type": "object",
  "additionalProperties": false,
  "anyOf": [
    { "required": ["path"] },
    { "required": ["filePath"] },
    { "required": ["file_path"] }
  ],
  "required": ["target_snippet", "replacement_snippet", "original_hash"],
  "properties": {
    "path": { "type": "string", "minLength": 1, "pattern": "\\S" },
    "filePath": { "type": "string", "minLength": 1, "pattern": "\\S", "description": "Alias for path." },
    "file_path": { "type": "string", "minLength": 1, "pattern": "\\S", "description": "Alias for path." },
    "target_snippet": { "type": "string", "minLength": 1 },
    "replacement_snippet": { "type": "string" },
    "original_hash": { "type": "string", "minLength": 1, "pattern": "\\S" }
  }
}
""";

    private readonly FileOperationsService _operations;

    public ReplaceInFileTool(FileOperationsService operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public string Name => "replace_in_file";
    public string Description =>
        "Replaces a specific snippet of code in a file. Path argument: path, filePath, or file_path (at least one; when more than one is set they must be equal). "
        + "CRITICAL: You MUST provide the 'original_hash' exactly as returned by your last 'read_file' call. "
        + "If you do not have the current hash, you MUST call 'read_file' first to get it.";
    public string InputSchemaJson => Schema;

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ToolArguments.RejectUnknownProperties(
            arguments,
            "path",
            "filePath",
            "file_path",
            "target_snippet",
            "replacement_snippet",
            "original_hash");
        var path = ToolArguments.GetRequiredPath(arguments);
        // FS-11 (1.11.0): the three text arguments have three different empty rules.
        // replacement_snippet accepts "" and any whitespace (deletion); target_snippet is
        // non-empty by Length, so a whitespace-only target is a legal exact match;
        // original_hash is non-whitespace, since whitespace is not a hash — and no argument
        // is trimmed. The schema mirrors exactly this for these three arguments:
        // replacement_snippet and target_snippet are plain "type":"string" (+ minLength 1
        // for the target), while original_hash additionally carries pattern "\S". The
        // asymmetry is deliberate: a whitespace-only target matches literally, a
        // whitespace-only hash can never match any digest. Path aliases are a different
        // rule: a whitespace-only path is not a path (PathPolicy), so their schema is
        // minLength 1 plus pattern "\S", and ToolArguments rejects that set before any write.
        var targetSnippet = GetRequiredString(arguments, "target_snippet", RequiredText.NonEmpty);
        var replacementSnippet = GetRequiredString(arguments, "replacement_snippet", RequiredText.Any);
        var originalHash = GetRequiredString(arguments, "original_hash", RequiredText.NonWhitespace);

        var (resolvedPath, newText, md5, newHash, replaceIndex) = await _operations.ReplaceInFileAsync(
            path,
            targetSnippet,
            replacementSnippet,
            originalHash,
            cancellationToken);

        var result = new ReplaceInFileToolResult(
            Status: "success",
            Path: resolvedPath,
            Md5: md5,
            Sha256: newHash,
            NewHash: newHash,
            Snippet: BuildSnippet(newText, replaceIndex));

        return JsonSerializer.Serialize(result, McpJsonContext.Default.ReplaceInFileToolResult);
    }

    private enum RequiredText { Any, NonEmpty, NonWhitespace }

    private static string GetRequiredString(JsonElement arguments, string propertyName, RequiredText rule)
    {
        // Missing, JSON null and a wrong type are the same failure: no implicit default.
        if (!arguments.TryGetProperty(propertyName, out var node)
            || node.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Missing required argument: {propertyName}.");
        }

        var value = node.GetString()!;
        var invalid = rule switch
        {
            RequiredText.NonEmpty => value.Length == 0,
            RequiredText.NonWhitespace => string.IsNullOrWhiteSpace(value),
            _ => false
        };
        if (invalid)
        {
            throw new ArgumentException($"Argument {propertyName} cannot be empty.");
        }

        return value;
    }

    // The window is anchored on the offset the edit actually happened at, in the LF-normalized
    // result. Searching the result for the replacement text — the pre-1.11.0 behaviour — has no
    // position at all for a deletion ("" matches at index 0) and picks the wrong window when the
    // replacement text already occurred earlier in the file.
    private static string BuildSnippet(string text, int replaceIndex)
    {
        if (text.Length <= MaxSnippetLength)
        {
            return text;
        }

        var anchor = Math.Clamp(replaceIndex, 0, text.Length);
        var start = Math.Max(0, anchor - (MaxSnippetLength / 2));
        var length = Math.Min(MaxSnippetLength, text.Length - start);
        return text.Substring(start, length);
    }
}
