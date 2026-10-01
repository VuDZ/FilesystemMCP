using System.Buffers;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilesystemMcp;

internal sealed class SearchTool : IMcpTool
{
    private const int BinaryProbeLength = 512;
    private const int MaxMatches = 50;
    private const string DefaultMask = "*";
    private const string Schema = """
{
  "type": "object",
  "additionalProperties": false,
  "required": ["regex"],
  "properties": {
    "regex": { "type": "string", "minLength": 1 },
    "file_mask": { "type": "string", "minLength": 1 }
  }
}
""";

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "build",
        "debug",
        "release"
    };

    private readonly PathPolicy _policy;
    private string _workspaceRoot => _policy.Root;

    public SearchTool(string workspaceRoot) : this(new PathPolicy(workspaceRoot)) { }

    public SearchTool(PathPolicy policy) => _policy = policy;

    public string Name => "search";
    public string Description => "Searches for a regex pattern in files. ALWAYS use this to find function definitions or variable usages instead of guessing file paths. Returns max 50 results.";
    public string InputSchemaJson => Schema;

    public async Task<string> ExecuteAsync(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        if (!arguments.TryGetProperty("regex", out var regexNode)
            || regexNode.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || regexNode.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Missing required argument: regex.");
        }

        var regexPattern = regexNode.GetString();
        if (string.IsNullOrWhiteSpace(regexPattern))
        {
            throw new ArgumentException("Argument regex cannot be empty.");
        }

        var fileMask = DefaultMask;
        if (arguments.TryGetProperty("file_mask", out var maskNode) && maskNode.ValueKind != JsonValueKind.Null)
        {
            if (maskNode.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("file_mask must be a string.");
            }

            var mask = maskNode.GetString();
            if (string.IsNullOrWhiteSpace(mask))
            {
                throw new ArgumentException("file_mask cannot be empty.");
            }

            fileMask = mask;
        }

        Regex regex;
        try
        {
            regex = new Regex(
                regexPattern,
                RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid regex: {ex.Message}", nameof(arguments));
        }

        return await SearchAsync(regex, fileMask);
    }

    private async Task<string> SearchAsync(Regex regex, string fileMask)
    {
        var result = new List<(string Path, int Line)>();
        var skipped = new List<(string Path, string Code)>();
        var visited = new HashSet<NativePath.DirectoryIdentity>();
        var pending = new Stack<string>();
        pending.Push(".");
        while (pending.Count > 0 && result.Count < MaxMatches)
        {
            var requested = pending.Pop();
            string directory;
            NativePath.DirectoryIdentity identity;
            try
            {
                directory = _policy.Resolve(requested);
                identity = NativePath.GetDirectoryIdentity(directory);
            }
            catch (PathPolicyException ex) { skipped.Add((requested, ex.Code)); continue; }
            if (!visited.Add(identity)) { skipped.Add((requested, "already_visited")); continue; }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                // Keep the logical alias, including when a link points outside.
                // Matches can be passed straight back to read_file.
                var relative = requested == "." ? Path.GetFileName(entry) : Path.Combine(requested, Path.GetFileName(entry));
                string physical;
                try { physical = _policy.Resolve(relative); }
                catch (PathPolicyException ex) { skipped.Add((relative, ex.Code)); continue; }
                if (Directory.Exists(physical))
                {
                    if (!ShouldSkipDirectory(entry)) pending.Push(relative);
                    continue;
                }
                if (!FileSystemName.MatchesSimpleExpression(fileMask, Path.GetFileName(entry), ignoreCase: true)) continue;
                if (!await IsTextFileAsync(physical)) continue;
                await CollectMatchesAsync(physical, relative, regex, result);
                if (result.Count >= MaxMatches) break;
            }
        }
        return SerializeMatches(result, skipped);
    }

    private static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return !string.IsNullOrEmpty(name) && SkippedDirectories.Contains(name);
    }

    private static async Task<bool> IsTextFileAsync(string filePath)
    {
        await using var stream = new FileStream(
            filePath,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.ReadWrite,
                Options = FileOptions.SequentialScan
            });

        var buffer = new byte[BinaryProbeLength];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, BinaryProbeLength));
        for (var i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == 0)
            {
                return false;
            }
        }

        return true;
    }

    private async Task CollectMatchesAsync(string filePath, string logicalPath, Regex regex, List<(string Path, int Line)> result)
    {
        await using var stream = new FileStream(
            filePath,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.ReadWrite,
                Options = FileOptions.SequentialScan
            });

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        string? line;

        while ((line = await reader.ReadLineAsync()) is not null && result.Count < MaxMatches)
        {
            lineNumber++;
            var lineMatches = regex.Matches(line);
            if (lineMatches.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < lineMatches.Count && result.Count < MaxMatches; i++)
            {
                result.Add((logicalPath, lineNumber));
            }
        }
    }

    private static string SerializeMatches(IReadOnlyList<(string Path, int Line)> matches, IReadOnlyList<(string Path, string Code)> skipped)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("matches");
            foreach (var match in matches)
            {
                writer.WriteStartObject();
                writer.WriteString("path", match.Path);
                writer.WriteNumber("line", match.Line);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("skipped");
            foreach (var item in skipped)
            {
                writer.WriteStartObject();
                writer.WriteString("path", item.Path);
                writer.WriteString("code", item.Code);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("incomplete", skipped.Count > 0);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
