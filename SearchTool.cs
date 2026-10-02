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
    private const int MaxSkippedDetails = 50;
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

    /// <summary>
    /// Deterministic fault-injection seam for tests: invoked after the binary probe
    /// and before decode while the single read stream is still open.
    /// </summary>
    internal Action<string>? AfterProbe { get; set; }

    /// <summary>Deterministic test seam: invoked with the resolved path right before entry kind detection.</summary>
    internal Action<string>? BeforeEntryKindProbe { get; set; }

    /// <summary>Deterministic test seam: invoked with the resolved directory right before its identity is opened.</summary>
    internal Action<string>? BeforeDirectoryIdentity { get; set; }

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

    /// <summary>
    /// A pattern that exhausts the per-match deadline is a bounded-resource refusal caused
    /// by the client's own request, not a server defect: it is reported as the operational
    /// FS-05 <c>resource_limit</c>, so the client knows to simplify the pattern instead of
    /// retrying it unchanged. Every other failure stays an unexpected defect.
    /// </summary>
    private async Task<string> SearchAsync(Regex regex, string fileMask)
    {
        try
        {
            return await SearchCoreAsync(regex, fileMask);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new OperationalException(
                ToolErrorCodes.ResourceLimit, ToolErrorMessages.ForCode(ToolErrorCodes.ResourceLimit));
        }
    }

    private async Task<string> SearchCoreAsync(Regex regex, string fileMask)
    {
        var result = new List<(string Path, int Line)>();
        var skipped = new List<(string Path, string Reason)>();
        var skippedCount = 0;
        var visited = new HashSet<NativePath.DirectoryIdentity>();

        // The workspace root is resolved once. A root that cannot be resolved or
        // listed is an operational failure, never a successful empty search.
        var rootDirectory = _policy.Resolve(".");
        var pending = new Stack<string>();
        pending.Push(".");
        while (pending.Count > 0 && result.Count < MaxMatches)
        {
            var requested = pending.Pop();
            var isRoot = requested == ".";
            string directory;
            NativePath.DirectoryIdentity identity;
            try
            {
                directory = isRoot ? rootDirectory : _policy.Resolve(requested);
                BeforeDirectoryIdentity?.Invoke(directory);
                identity = NativePath.GetDirectoryIdentity(directory, preserveNativeError: true);
            }
            catch (PathPolicyException ex)
            {
                if (isRoot) throw FileErrorClassifier.Translate(ex);
                AddSkip(requested, ex.Code);
                continue;
            }
            catch (Exception ex)
            {
                if (isRoot) throw FileErrorClassifier.Translate(ex);
                if (!FileErrorClassifier.TryGetCode(ex, out var code)) throw;
                AddSkip(requested, code);
                continue;
            }

            if (!visited.Add(identity))
            {
                AddSkip(requested, "already_visited");
                continue;
            }

            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (Exception ex)
            {
                if (isRoot) throw FileErrorClassifier.Translate(ex);
                if (!FileErrorClassifier.TryGetCode(ex, out var code)) throw;
                AddSkip(requested, code);
                continue;
            }

            foreach (var entry in entries)
            {
                // Keep the logical alias, including when a link points outside.
                // Matches can be passed straight back to read_file.
                var relative = requested == "." ? Path.GetFileName(entry) : Path.Combine(requested, Path.GetFileName(entry));
                string physical;
                try { physical = _policy.Resolve(relative); }
                catch (PathPolicyException ex) { AddSkip(relative, ex.Code); continue; }
                catch (Exception ex)
                {
                    if (!FileErrorClassifier.TryGetCode(ex, out var code)) throw;
                    AddSkip(relative, code);
                    continue;
                }

                BeforeEntryKindProbe?.Invoke(physical);
                bool isDirectory;
                try
                {
                    isDirectory = (File.GetAttributes(physical) & FileAttributes.Directory) != 0;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException) throw;
                    // An entry that cannot be probed (access denied, locked or
                    // vanished) must be reported BEFORE the mask check: a narrow
                    // mask must not silently drop it and leave incomplete=false.
                    if (!FileErrorClassifier.TryGetCode(ex, out var kindCode)) throw;
                    AddSkip(relative, kindCode);
                    continue;
                }

                if (isDirectory)
                {
                    if (!ShouldSkipDirectory(entry)) pending.Push(relative);
                    continue;
                }

                if (!FileSystemName.MatchesSimpleExpression(fileMask, Path.GetFileName(entry), ignoreCase: true)) continue;
                try
                {
                    await SearchFileAsync(physical, relative, regex, result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!FileErrorClassifier.TryGetCode(ex, out var code)) throw;
                    AddSkip(relative, code);
                }

                if (result.Count >= MaxMatches) break;
            }
        }

        return SerializeMatches(result, skipped, skippedCount);

        void AddSkip(string path, string reason)
        {
            // The detail list is bounded; skipped_count always reflects the full total.
            skippedCount++;
            if (skipped.Count < MaxSkippedDetails)
            {
                skipped.Add((path, reason));
            }
        }
    }

    private static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return !string.IsNullOrEmpty(name) && SkippedDirectories.Contains(name);
    }

    // One open read stream covers the binary probe, the decode and the search, so a
    // rename/delete between those steps can no longer abort the whole operation.
    private async Task SearchFileAsync(string filePath, string logicalPath, Regex regex, List<(string Path, int Line)> result)
    {
        await using var stream = FileTextHelper.OpenReadStream(filePath);
        var probe = new byte[BinaryProbeLength];
        var bytesRead = await stream.ReadAsync(probe.AsMemory(0, BinaryProbeLength));
        for (var i = 0; i < bytesRead; i++)
        {
            // FS-09 owns BOM-aware encoding detection; this NUL probe is unchanged.
            if (probe[i] == 0)
            {
                return;
            }
        }

        AfterProbe?.Invoke(filePath);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
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

    private static string SerializeMatches(
        IReadOnlyList<(string Path, int Line)> matches,
        IReadOnlyList<(string Path, string Reason)> skipped,
        int skippedCount)
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
            // truncated is only the result/budget cap; incomplete covers skipped entries.
            writer.WriteBoolean("truncated", matches.Count >= MaxMatches);
            writer.WriteBoolean("incomplete", skippedCount > 0);
            writer.WriteNumber("skipped_count", skippedCount);
            writer.WriteStartArray("skipped");
            foreach (var item in skipped)
            {
                writer.WriteStartObject();
                writer.WriteString("path", item.Path);
                // reason is the canonical FS-04 field; code is the FS-01 compatibility alias.
                writer.WriteString("reason", item.Reason);
                writer.WriteString("code", item.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
