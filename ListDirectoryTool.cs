using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FilesystemMcp;

internal sealed class ListDirectoryTool : IMcpTool
{
    private const string Head = "{\"entries\":[";
    private const string PlainTail = "]}";
    /// <summary>The only cap this tool reports; the value is the FS-07 wire token.</summary>
    internal const string TruncationReasonMaxResponseChars = "max_response_chars";
    /// <summary>
    /// Lower bound on the payload one entry can occupy: the shortest rendered entry is
    /// <c>{"name":"","type":""}</c> plus a separator, and the directory itself can never
    /// deliver more entries than the budget divided by that floor plus the final one.
    /// </summary>
    private const long MinEntryPayloadChars = 21;
    private const string TruncatedTail = "],\"truncated\":true,\"truncation_reason\":\"" + TruncationReasonMaxResponseChars + "\"";
    private const string TruncationReport = "{\"truncated\":true,\"truncation_reason\":\"" + TruncationReasonMaxResponseChars + "\"}";

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
    "file_path": { "type": "string", "minLength": 1, "pattern": "\\S", "description": "Alias for path." }
  }
}
""";

    private readonly PathPolicy _policy;
    private readonly ResourceBudget _budget;
    private string _workspaceRoot => _policy.Root;

    /// <summary>
    /// Deterministic test seam for the FS-07 response budget: replaces
    /// <see cref="ResourceBudget.MaxResponseChars"/> so a unit test can force truncation
    /// with a tiny value instead of a multi-megabyte directory.
    /// </summary>
    internal long? MaxOutputCharsOverride { get; set; }

    /// <summary>
    /// Deterministic test seam for the FS-07 entry cap; null in production, where the cap
    /// is derived from the budget.
    /// </summary>
    internal int? MaxEntriesOverride { get; set; }

    public ListDirectoryTool(string workspaceRoot) : this(new PathPolicy(workspaceRoot)) { }

    public ListDirectoryTool(PathPolicy policy) : this(policy, ResourceBudget.Default) { }

    public ListDirectoryTool(PathPolicy policy, ResourceBudget budget)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    public string Name => "list_directory";
    public string Description =>
        "Lists files and folders in a directory. Path argument: path, filePath, or file_path (at least one; when more than one is set they must be equal). "
        + "ALWAYS use this to explore the project structure before assuming file paths.";
    public string InputSchemaJson => Schema;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ToolArguments.RejectUnknownProperties(arguments, "path", "filePath", "file_path");
        var path = ToolArguments.GetRequiredPath(arguments);

        var resolved = _policy.Resolve(path);

        // FS-07: names are materialized up to a cap derived from the response budget, never
        // the whole directory. One entry needs at least one name character plus its JSON
        // envelope, so the budget divided by that floor is an upper bound on what could
        // ever be delivered; enumerating further would only consume memory.
        var entryCap = MaxEntriesOverride ?? (int)Math.Clamp(
            (_budget.MaxResponseChars / MinEntryPayloadChars) + 1, 1, int.MaxValue);

        List<string> entries;
        var truncatedByCap = false;
        try
        {
            // The token is observed per entry, so a directory with very many entries
            // stops promptly instead of finishing an unbounded enumeration.
            entries = [];
            foreach (var entry in EnumerateWithCancellation(resolved, cancellationToken))
            {
                if (entries.Count >= entryCap)
                {
                    // The cap is reached: stop enumerating rather than count what can never be
                    // delivered. Nothing here reports a total, so there is nothing to finish
                    // counting for — finishing would only spend the deadline on a number that
                    // cannot be sent.
                    truncatedByCap = true;
                    break;
                }

                entries.Add(entry);
            }

            entries.Sort(static (left, right) => PathPolicy.Comparer.Compare(left, right));
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            // Recognized failures become operational codes; genuinely unexpected
            // ones (e.g. ERROR_DIRECTORY when the path is a file) are left to
            // propagate as an internal error instead of being mislabeled.
            throw FileErrorClassifier.Translate(ex);
        }

        return Task.FromResult(Render(entries, truncatedByCap, cancellationToken));
    }

    private static IEnumerable<string> EnumerateWithCancellation(string directory, CancellationToken cancellationToken)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    /// <summary>
    /// Builds the FS-07 bounded payload incrementally: one entry is added only while the
    /// complete object (closing braces included) still fits
    /// <see cref="ResourceBudget.MaxResponseChars"/>. The budget is counted in the
    /// payload's own characters, so the untruncated shape is byte-for-byte the one this
    /// tool always produced, and a truncated payload is always parseable JSON — the text
    /// is never cut.
    /// </summary>
    /// <remarks>
    /// The budget is a target, not an absolute: when it cannot hold even the truncation
    /// report, the report is emitted anyway and exceeds it. That is the contract's floor
    /// (FS-07 R7) — the report is what makes the truncation machine-readable and carries the
    /// cause the transport reads when it replaces the frame — so this method returns the most
    /// informative payload it can and leaves bounding the frame to the transport.
    /// <para>
    /// There is deliberately no "how many entries are there really" field. Reporting the true
    /// total would mean finishing the enumeration after the materialization cap stopped it,
    /// and the only bound on that would be the deadline — which destroys the listing it was
    /// meant to describe. A partial listing therefore says <c>truncated:true</c> and nothing
    /// more; a count belongs to the paging work in B-03, where the count itself can be bounded.
    /// </para>
    /// </remarks>
    private string Render(List<string> entries, bool truncatedByCap, CancellationToken cancellationToken)
    {
        var limit = MaxOutputCharsOverride ?? _budget.MaxResponseChars;
        var fragments = new List<string>(Math.Min(entries.Count, 64));
        var payloadLength = Head.Length;
        var truncated = truncatedByCap;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                // Per-entry lock/acl/not-found surfaces as an operational code.
                throw FileErrorClassifier.Translate(ex);
            }

            var fragment = RenderEntry(Path.GetFileName(entry), KindOf(attributes));
            var separator = fragments.Count == 0 ? 0 : 1;
            if (payloadLength + separator + fragment.Length + PlainTail.Length > limit)
            {
                truncated = true;
                break;
            }

            fragments.Add(fragment);
            payloadLength += separator + fragment.Length;
        }

        if (!truncated && payloadLength + PlainTail.Length > limit)
        {
            truncated = true;
        }

        var builder = new StringBuilder(payloadLength + TruncatedTail.Length);
        builder.Append(Head);
        for (var i = 0; i < fragments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(fragments[i]);
        }

        if (truncated)
        {
            // The report (`],"truncated":true,…`) is longer than the plain `]}` it replaces,
            // so trailing entries are dropped until the response fits. Entries are preferred
            // over the report for as long as the choice exists, because a truncated listing
            // without data is barely a listing at all.
            //
            // The floor is the report on its own, and that floor deliberately exceeds the
            // budget when the budget cannot hold it: the report is what makes the truncation
            // machine-readable and carries the cause the transport reads when it replaces the
            // frame, so emitting it is the honest outcome. This method therefore does NOT
            // always fit the budget — it always returns the most informative payload it can,
            // and the transport bounds the frame.
            while (true)
            {
                var tail = ReportTail(payloadLength, limit);
                if (tail is not null && payloadLength + tail.Length <= limit)
                {
                    builder.Append(tail);
                    return builder.ToString();
                }

                if (fragments.Count == 0)
                {
                    // Floor: the truncation report with its cause, never a bare
                    // `{"truncated":true}`. The transport reads the cause out of this field
                    // when it has to replace an over-budget frame, so a floor without it would
                    // tell the client the listing was cut without saying why.
                    return TruncationReport;
                }

                payloadLength -= fragments[^1].Length + (fragments.Count > 1 ? 1 : 0);
                fragments.RemoveAt(fragments.Count - 1);
                Rebuild();
            }
        }

        builder.Append(PlainTail);
        return builder.ToString();

        // The longest truncation suffix the budget can still hold for this payload.
        static string? ReportTail(int payload, long budget) =>
            payload + TruncatedTail.Length + 1 <= budget ? TruncatedTail + "}" : null;

        void Rebuild()
        {
            builder.Clear();
            builder.Append(Head);
            for (var i = 0; i < fragments.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(fragments[i]);
            }
        }
    }

    /// <summary>
    /// One entry object, rendered by the same writer/escaping the payload uses, so the
    /// running character count equals what the client receives (the budget is counted in
    /// UTF-16 code units of the payload, not in UTF-8 bytes).
    /// </summary>
    private static string RenderEntry(string name, string type)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("type", type);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string KindOf(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0 ? "link"
        : (attributes & FileAttributes.Directory) != 0 ? "directory" : "file";
}
