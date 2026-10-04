using System.Text;

namespace FilesystemMcp;

internal sealed class FileOperationsService
{
    public const int DefaultMaxLines = 1000;
    public const int AbsoluteMaxLines = 50_000;

    /// <summary>
    /// FS-07 read budgets. The default keeps every existing construction site working
    /// unchanged; the composition root replaces it with the validated startup budget,
    /// which a client can never raise through <c>tools/call</c>.
    /// </summary>
    internal ResourceBudget Budget { get; init; } = ResourceBudget.Default;

    /// <summary>Deterministic retry-observation seam for tests; null in production.</summary>
    internal Action<int>? BeforeReadRetry { get; set; }

    /// <summary>
    /// Deterministic stream seam for tests; null in production. When set, reads go
    /// through this factory instead of opening the resolved path, so chunking, line and
    /// byte budgets can be proven without a huge fixture.
    /// </summary>
    internal Func<string, Stream>? OpenReadStreamForTests { get; set; }

    private string WorkspaceRoot => _policy.Root;

    public FileOperationsService(string workspaceRoot) : this(new PathPolicy(workspaceRoot))
    {
    }

    public FileOperationsService(PathPolicy policy) => _policy = policy;

    public async Task<CreateFileResult> CreateFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var result = await TranslateAsync(() =>
            AtomicFileWriter.WriteTextAsync(_policy, path, content, true, cancellationToken));
        return new CreateFileResult(result.Path, result.Md5, result.Sha256);
    }

    public async Task<ReadFileResult> ReadFileAsync(
        string path,
        ReadFileOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolvedPath = _policy.Resolve(path);

        ValidateLineRange(options.StartLine, options.EndLine);
        var effectiveMaxLines = ResolveEffectiveMaxLines(options);

        if (options.StartLine.HasValue
            && options.EndLine.HasValue
            && options.EndLine.Value - options.StartLine.Value + 1 > effectiveMaxLines)
        {
            throw ResourceLimit();
        }

        var isFullFileRead = !options.StartLine.HasValue && !options.EndLine.HasValue;

        // One streaming pass replaces ReadCanonicalContentAsync + ComputeContentHashes +
        // ExtractRequestedContent: the hashes and the line count always cover the whole
        // file, while only the selected lines are materialized. The full-file path keeps
        // its prefix cap (allow_large_read/max_lines) as the materialization bound, so a
        // file over the line cap is refused after the scan instead of being turned into a
        // huge string first.
        //
        // textLimit is the second, independent bound. A line cap alone does not bound
        // memory when each line may be huge: a 1000-line file is 64 MiB at maxLineChars, so
        // the materialized text could reach the byte budget while maxResponseChars is one
        // megabyte. Passing the response budget here means such a file is refused during
        // the scan, instead of being built in full and then replaced by the transport
        // (which measured a 650 MB peak for a 64 MiB file before this bound existed).
        var content = await FileContentReader.ReadCanonicalAsync(
            resolvedPath,
            options.StartLine,
            options.EndLine,
            captureFullText: isFullFileRead,
            Budget,
            cancellationToken,
            OpenReadStreamForTests,
            BeforeReadRetry,
            maxLines: isFullFileRead ? effectiveMaxLines : null,
            textLimit: MaterializationLimit(effectiveMaxLines));

        if (isFullFileRead)
        {
            if (!options.AllowLargeRead && content.TotalLines > DefaultMaxLines)
            {
                throw ResourceLimit();
            }

            if (options.AllowLargeRead
                && !options.MaxLines.HasValue
                && content.TotalLines > AbsoluteMaxLines)
            {
                throw ResourceLimit();
            }
        }

        return new ReadFileResult(
            resolvedPath,
            content.Text,
            content.Md5,
            content.Sha256,
            content.TotalLines,
            content.StartLine,
            content.EndLine,
            content.Truncated,
            content.HasMore);
    }

    public static int ResolveEffectiveMaxLines(ReadFileOptions options)
    {
        // FS-10: max_lines without the explicit opt-in is an invalid argument, not a value
        // to silently ignore — the client believes it narrowed the read when it did not.
        if (options.MaxLines.HasValue && !options.AllowLargeRead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "max_lines requires allow_large_read=true.");
        }

        if (!options.AllowLargeRead)
        {
            return DefaultMaxLines;
        }

        if (!options.MaxLines.HasValue)
        {
            return AbsoluteMaxLines;
        }

        if (options.MaxLines.Value < 1 || options.MaxLines.Value > AbsoluteMaxLines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"max_lines must be between 1 and {AbsoluteMaxLines}.");
        }

        return options.MaxLines.Value;
    }

    // FS-11 (1.11.0): ReplaceIndex is the canonical (LF-normalized) offset of the first exact
    // match that was replaced. It lets the tool anchor its response snippet on the edit itself.
    public async Task<(string Path, string NewText, string Md5, string NewHash, int ReplaceIndex)> ReplaceInFileAsync(
        string path,
        string targetSnippet,
        string replacementSnippet,
        string originalHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be provided.", nameof(path));
        }

        if (string.IsNullOrEmpty(targetSnippet))
        {
            throw new ArgumentException("targetSnippet cannot be empty.", nameof(targetSnippet));
        }

        if (string.IsNullOrWhiteSpace(originalHash))
        {
            throw new ArgumentException("originalHash cannot be empty.", nameof(originalHash));
        }

        var result = await TranslateAsync(() =>
            AtomicFileWriter.ReplaceAsync(_policy, path, targetSnippet, replacementSnippet, originalHash, cancellationToken));
        // Only a replace reports an edit position; a whole-text write has none.
        return (result.Path, result.Text, result.Md5, result.Sha256, result.ReplaceIndex!.Value);
    }

    private static async Task<T> TranslateAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            var translated = FileErrorClassifier.Translate(ex);
            if (ReferenceEquals(translated, ex))
            {
                throw;
            }

            throw translated;
        }
    }

    /// <summary>
    /// The largest selected text this read may materialize, in UTF-16 code units. It is the
    /// response budget, because a larger payload could never be delivered: the transport
    /// would replace it with a bounded refusal after it had already been built.
    /// </summary>
    /// <remarks>
    /// It is deliberately not floored at <c>maxLineChars + 1</c>. That floor looked like it
    /// kept an oversized line diagnosable, but the line check runs first anyway — the reader
    /// rejects a line longer than <c>maxLineChars</c> while splitting it, before the text
    /// budget is ever consulted — so the floor only added a case where materialization
    /// exceeded the budget, contradicting the rule that text is not accumulated past it.
    /// </remarks>
    private int MaterializationLimit(int effectiveMaxLines)
    {
        var byResponse = Budget.MaxResponseChars > int.MaxValue ? int.MaxValue : (int)Budget.MaxResponseChars;
        var byLine = (long)Budget.MaxLineChars + 1;
        var byLines = effectiveMaxLines <= 0 ? byResponse : Math.Min(byResponse, (long)effectiveMaxLines * byLine);
        return (int)Math.Clamp(byLines, 1, int.MaxValue);
    }

    /// <summary>
    /// An existing read guard (FS-05 <c>resource_limit</c>) is an expected operational
    /// refusal, not an internal defect: nothing was written, and repeating the same
    /// request would fail again, so the code carries no retry advice.
    /// </summary>
    private static OperationalException ResourceLimit() =>
        new(ToolErrorCodes.ResourceLimit, ToolErrorMessages.ForCode(ToolErrorCodes.ResourceLimit));

    private static void ValidateLineRange(int? startLine, int? endLine)
    {
        if (!startLine.HasValue && !endLine.HasValue)
        {
            return;
        }

        if (!startLine.HasValue || !endLine.HasValue)
        {
            throw new ArgumentException("Both start_line and end_line must be provided together.");
        }

        if (startLine.Value < 1 || endLine.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(startLine), "Line numbers must be >= 1.");
        }

        if (startLine.Value > endLine.Value)
        {
            throw new ArgumentException("start_line cannot be greater than end_line.");
        }
    }

    private static string ReplaceFirst(string source, string target, string replacement, int index)
    {
        var builder = new StringBuilder(source.Length - target.Length + replacement.Length);
        builder.Append(source, 0, index);
        builder.Append(replacement);
        builder.Append(source, index + target.Length, source.Length - index - target.Length);
        return builder.ToString();
    }

    private readonly PathPolicy _policy;
}
