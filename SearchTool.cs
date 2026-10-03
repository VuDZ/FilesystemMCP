using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilesystemMcp;

internal sealed class SearchTool : IMcpTool
{
    private const int MaxMatches = 50;
    private const int MaxSkippedDetails = 50;
    private const string DefaultMask = "*";

    /// <summary>The cap that ended a traversal or the payload, as its <c>truncation_reason</c> value.</summary>
    internal const string TruncationReasonSearchMaxFiles = "search_max_files";
    internal const string TruncationReasonSearchMaxDirectories = "search_max_directories";
    internal const string TruncationReasonMaxResponseChars = "max_response_chars";
    internal const string TruncationReasonOperationTimeout = "operation_timeout";

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
    private readonly ResourceBudget _budget;
    private string _workspaceRoot => _policy.Root;

    /// <summary>
    /// Deterministic fault-injection seam for tests: invoked after the signature probe
    /// and before the decode while the single read stream is still open.
    /// </summary>
    internal Action<string>? AfterProbe { get; set; }

    /// <summary>
    /// Deterministic stream seam for tests; null in production. When set, a searched
    /// file is read through this factory instead of being opened by path, so short
    /// reads and a BOM split across chunks can be proven without a special file.
    /// </summary>
    internal Func<string, Stream>? OpenReadStreamForTests { get; set; }

    /// <summary>Deterministic test seam: invoked with the resolved path right before entry kind detection.</summary>
    internal Action<string>? BeforeEntryKindProbe { get; set; }

    /// <summary>Deterministic test seam: invoked with the resolved directory right before its identity is opened.</summary>
    internal Action<string>? BeforeDirectoryIdentity { get; set; }

    /// <summary>
    /// Deterministic test seam: invoked with the logical directory path once that
    /// directory has been counted against <c>searchMaxDirectories</c> and before its
    /// entries are enumerated. A test can therefore assert the exact number of
    /// directories the traversal visited instead of timing it.
    /// </summary>
    internal Action<string>? BeforeDirectoryVisited { get; set; }

    /// <summary>
    /// Deterministic test seam: invoked with the physical file path once that file has
    /// been counted against <c>searchMaxFiles</c> and immediately before it is read.
    /// </summary>
    internal Action<string>? BeforeFileSearch { get; set; }

    /// <summary>Deterministic test seam: invoked with the logical path each time a match is recorded.</summary>
    internal Action<string>? MatchRecorded { get; set; }

    /// <summary>
    /// Deterministic test seam for the response budget: replaces
    /// <see cref="ResourceBudget.MaxResponseChars"/> so a unit test can force payload
    /// trimming with a tiny value instead of a multi-megabyte fixture.
    /// </summary>
    internal long? MaxOutputCharsOverride { get; set; }

    /// <summary>
    /// Deterministic test seam that classifies an observed cancellation. The transport
    /// links the peer's <c>notifications/cancelled</c> and the FS-07 deadline into one
    /// token, so the cause is not readable off the token itself; production leaves this
    /// null and the tool decides by timing the operation (see <see cref="DeadlineScope"/>),
    /// while a test can force either outcome without a sleep.
    /// </summary>
    internal Func<CancellationToken, bool>? IsDeadlineExceeded { get; set; }

    public SearchTool(string workspaceRoot) : this(new PathPolicy(workspaceRoot)) { }

    public SearchTool(PathPolicy policy) : this(policy, ResourceBudget.Default) { }

    public SearchTool(PathPolicy policy, ResourceBudget budget)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    public string Name => "search";
    public string Description => "Searches for a regex pattern in files. ALWAYS use this to find function definitions or variable usages instead of guessing file paths. Returns max 50 results.";
    public string InputSchemaJson => Schema;

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
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

        return await SearchAsync(regex, fileMask, cancellationToken);
    }

    /// <summary>
    /// A pattern that exhausts the per-match deadline is a bounded-resource refusal caused
    /// by the client's own request, not a server defect: it is reported as the operational
    /// FS-05 <c>resource_limit</c>, so the client knows to simplify the pattern instead of
    /// retrying it unchanged. Every other failure stays an unexpected defect.
    /// </summary>
    private async Task<string> SearchAsync(Regex regex, string fileMask, CancellationToken cancellationToken)
    {
        try
        {
            return await SearchCoreAsync(regex, fileMask, cancellationToken);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new OperationalException(
                ToolErrorCodes.ResourceLimit, ToolErrorMessages.ForCode(ToolErrorCodes.ResourceLimit));
        }
    }

    private async Task<string> SearchCoreAsync(Regex regex, string fileMask, CancellationToken cancellationToken)
    {
        var result = new List<(string Path, int Line)>();
        var skipped = new List<(string Path, string Reason)>();
        var skippedCount = 0;
        var visited = new HashSet<NativePath.DirectoryIdentity>();
        string? truncationReason = null;

        // The workspace root is resolved once. A root that cannot be resolved or
        // listed is an operational failure, never a successful empty search.
        var rootDirectory = _policy.Resolve(".");
        var pending = new Stack<string>();
        pending.Push(".");

        // FS-07 traversal budgets: every directory whose identity is visited and every
        // file that reaches matching is counted here, and reaching either budget stops
        // the traversal with a partial result instead of an error.
        var visitedDirectories = 0L;
        var visitedFiles = 0L;

        var token = cancellationToken;

        try
        {
            while (pending.Count > 0 && result.Count < MaxMatches)
            {
                token.ThrowIfCancellationRequested();
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

                visitedDirectories++;
                BeforeDirectoryVisited?.Invoke(requested);

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

                var fileBudgetReached = false;
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();

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

                    // FS-07 file budget: a file is counted exactly where it would be
                    // searched — after the mask check — and the file that consumes the
                    // last slot is still searched.
                    if (visitedFiles >= _budget.SearchMaxFiles)
                    {
                        truncationReason ??= TruncationReasonSearchMaxFiles;
                        fileBudgetReached = true;
                        break;
                    }

                    visitedFiles++;
                    BeforeFileSearch?.Invoke(physical);
                    try
                    {
                        var outcome = await SearchFileAsync(physical, relative, regex, MaxMatches - result.Count, token);
                        if (outcome.SkipReason is not null)
                        {
                            AddSkip(relative, outcome.SkipReason);
                        }

                        // Matches survive a budget skip — the file is text and part of it
                        // was legitimately searched — but not a classification skip: a
                        // binary or undecodable file contributes nothing, exactly like
                        // read_file refuses it outright.
                        foreach (var match in outcome.Matches)
                        {
                            result.Add(match);
                            MatchRecorded?.Invoke(match.Path);
                        }
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

                // Reaching either traversal budget stops the whole walk. The directory (and
                // the file) that consumes the last slot is still fully processed, so the
                // counters never exceed their budget; every later unit is dropped, which is
                // what the reported partial result means.
                if (visitedDirectories >= _budget.SearchMaxDirectories)
                {
                    truncationReason ??= TruncationReasonSearchMaxDirectories;
                    break;
                }

                if (fileBudgetReached || visitedFiles >= _budget.SearchMaxFiles)
                {
                    truncationReason ??= TruncationReasonSearchMaxFiles;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (IsDeadlineCancellation(token))
        {
            // The FS-07 deadline is a bounded-resource outcome, not a failure: the client
            // gets the matches found so far, reported as truncated/incomplete with
            // truncation_reason=operation_timeout. A peer cancellation
            // (notifications/cancelled, EOF or shutdown) is deliberately NOT caught here:
            // it stays an OperationCanceledException, which FS-05 maps to `cancelled`.
            truncationReason = TruncationReasonOperationTimeout;
        }

        return SerializeMatches(result, skipped, skippedCount, truncationReason);

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

    /// <summary>
    /// Decides whether an observed cancellation is the FS-07 deadline or the peer's own
    /// cancellation. The transport links both sources into one token, so the cause is read
    /// from <see cref="RequestDeadline"/> — the very token the transport scheduled — and
    /// never from elapsed wall-clock time: an earlier version classified by elapsed time
    /// and reported a peer cancel arriving in the last 250 ms of the budget as a
    /// successful <c>operation_timeout</c> partial result. A test may still classify it
    /// explicitly through <see cref="IsDeadlineExceeded"/>.
    /// </summary>
    private bool IsDeadlineCancellation(CancellationToken token) =>
        IsDeadlineExceeded is { } classify ? classify(token) : RequestDeadline.IsDeadline();

    private static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return !string.IsNullOrEmpty(name) && SkippedDirectories.Contains(name);
    }
    // One open read stream covers the signature probe, the decode and the search, so a
    // rename/delete between those steps can no longer abort the whole operation.
    /// <returns>
    /// The per-file outcome. The skip reason is null when the file was scanned;
    /// otherwise it is the FS-05 code that made search skip it — <c>binary_file</c> or
    /// <c>unsupported_encoding</c> from the shared decoder, or <c>resource_limit</c> for
    /// a file over <c>maxFileBytes</c> or a line over <c>maxLineChars</c>. The caller
    /// reports it through the FS-04 skip channel. <see cref="SearchFileOutcome.Matches"/>
    /// carries the lines matched before a budget skip; a classification skip discards
    /// them, because its verdict says the file was never text at all.
    /// </returns>
    private async Task<SearchFileOutcome> SearchFileAsync(
        string filePath,
        string logicalPath,
        Regex regex,
        int matchCap,
        CancellationToken cancellationToken)
    {
        var matches = new List<(string Path, int Line)>();
        try
        {
            // FS-09: search decodes through the same streaming scanner read_file uses
            // (BOM precedence, strict decode, deferred binary verdict, byte and line
            // budgets), so a UTF-16/UTF-32 file with a BOM is searchable text here too
            // and an invalid sequence anywhere — including past the probe — is a skip,
            // never a silently replaced U+FFFD match. The whole file is scanned even
            // after the match cap is reached, because the binary and encoding verdicts
            // are only final at end-of-stream.
            await FileContentReader.SearchLinesAsync(
                filePath,
                _budget,
                (lineNumber, line) =>
                {
                    if (matches.Count >= matchCap)
                    {
                        return;
                    }

                    var lineMatches = regex.Matches(line);
                    for (var i = 0; i < lineMatches.Count && matches.Count < matchCap; i++)
                    {
                        matches.Add((logicalPath, lineNumber));
                    }
                },
                afterHeadProbe: () => AfterProbe?.Invoke(filePath),
                openStream: OpenReadStreamForTests,
                cancellationToken).ConfigureAwait(false);
            return new SearchFileOutcome(null, matches);
        }
        catch (MutationException ex) when (ex.Code is ToolErrorCodes.BinaryFile or ToolErrorCodes.UnsupportedEncoding)
        {
            // The verdict is only final at end-of-stream, so it can arrive after matches
            // were collected: a NUL or an invalid sequence late in the file reclassifies
            // everything found in it. read_file refuses the same file outright, so the
            // shared decoder must not report matches from it either.
            return new SearchFileOutcome(ex.Code, []);
        }
        catch (ResourceLimitException)
        {
            // A budget skip is different: the file is text and part of it was searched,
            // so the matches collected before the oversized value stay (FS-07).
            return new SearchFileOutcome(ToolErrorCodes.ResourceLimit, matches);
        }
    }

    private readonly record struct SearchFileOutcome(string? SkipReason, List<(string Path, int Line)> Matches);

    /// <summary>
    /// Renders the FS-04 result object inside the FS-07 response budget. The budget is
    /// counted in the payload's own characters (what the client receives as text), and it
    /// is never applied by cutting JSON text: whole matches are dropped first, then whole
    /// skipped details, so every emitted payload is complete, parseable JSON.
    /// </summary>
    private string SerializeMatches(
        IReadOnlyList<(string Path, int Line)> matches,
        IReadOnlyList<(string Path, string Reason)> skipped,
        int skippedCount,
        string? truncationReason)
    {
        var limit = MaxOutputCharsOverride ?? _budget.MaxResponseChars;
        var matchLimit = matches.Count;
        var skipLimit = skipped.Count;
        var reason = truncationReason;
        var trimmed = false;

        while (true)
        {
            var payload = Render(matches, matchLimit, skipped, skipLimit, skippedCount, reason, trimmed);
            if (payload.Length <= limit)
            {
                return payload;
            }

            // The payload did not fit. The response budget is now the cap that bounded it,
            // but it must not erase a cause that already explains why the search is partial:
            // a partial result by deadline or by traversal cap stays that way, and losing it
            // would tell the client to narrow a request that was already cut for another
            // reason. The budget reason therefore applies only when nothing else applies.
            trimmed = true;
            reason ??= TruncationReasonMaxResponseChars;
            if (skipLimit > 0)
            {
                skipLimit--;
                continue;
            }

            if (matchLimit > 0)
            {
                matchLimit--;
                continue;
            }

            // Floor: the truncation report with its cause. This is the smallest payload this
            // tool emits, and it is deliberately never reduced to a bare `{"truncated":true}`:
            // the transport reads the cause out of this field when it has to replace an
            // over-budget frame, so a floor without it would leave the client knowing the
            // result was cut but not why. Staying small matters less than staying explicable.
            return "{\"truncated\":true,\"truncation_reason\":\"" + (reason ?? TruncationReasonMaxResponseChars) + "\"}";
        }
    }

    private static string Render(
        IReadOnlyList<(string Path, int Line)> matches,
        int matchCount,
        IReadOnlyList<(string Path, string Reason)> skipped,
        int skipCount,
        int skippedCount,
        string? truncationReason,
        bool trimmed)
    {
        // truncated is the result/budget cap; incomplete additionally covers skipped
        // entries and content the response budget had to drop. The 50-match cap keeps its
        // FS-04 meaning: truncated without incomplete.
        var truncated = trimmed || truncationReason is not null || matches.Count >= MaxMatches;
        var incomplete = trimmed || truncationReason is not null || skippedCount > 0;

        var buffer = new ArrayBufferWriter<byte>(8192);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("matches");
            for (var i = 0; i < matchCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("path", matches[i].Path);
                writer.WriteNumber("line", matches[i].Line);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("truncated", truncated);
            writer.WriteBoolean("incomplete", incomplete);
            writer.WriteNumber("skipped_count", skippedCount);
            writer.WriteStartArray("skipped");
            for (var i = 0; i < skipCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("path", skipped[i].Path);
                // reason is the canonical FS-04 field; code is the FS-01 compatibility alias.
                writer.WriteString("reason", skipped[i].Reason);
                writer.WriteString("code", skipped[i].Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (truncationReason is not null)
            {
                writer.WriteString("truncation_reason", truncationReason);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
