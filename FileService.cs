using System.Text;

namespace FilesystemMcp;

internal sealed class FileService
{
    public const int DefaultMaxLines = 1000;
    public const int AbsoluteMaxLines = 50_000;

    private readonly string _workspaceRoot;

    public FileService(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("WorkspaceRoot must be provided.", nameof(workspaceRoot));
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public async Task<ReadFileResult> ReadFileAsync(
        string path,
        ReadFileOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolvedPath = WorkspaceJail.ResolvePath(_workspaceRoot, path);

        ValidateLineRange(options.StartLine, options.EndLine);
        var effectiveMaxLines = ResolveEffectiveMaxLines(options);

        if (options.StartLine.HasValue
            && options.EndLine.HasValue
            && options.EndLine.Value - options.StartLine.Value + 1 > effectiveMaxLines)
        {
            throw new InvalidOperationException(
                $"Requested line range exceeds the limit of {effectiveMaxLines} lines. "
                + "Set allow_large_read=true and optionally max_lines to read a larger range.");
        }

        var canonicalContent = await FileTextHelper.ReadCanonicalContentAsync(resolvedPath, cancellationToken);
        var (md5, sha256) = FileTextHelper.ComputeContentHashes(canonicalContent);

        var isFullFileRead = !options.StartLine.HasValue && !options.EndLine.HasValue;
        var (text, totalLines) = FileTextHelper.ExtractRequestedContent(
            canonicalContent,
            options.StartLine,
            options.EndLine,
            effectiveMaxLines,
            isFullFileRead);

        if (isFullFileRead)
        {
            if (!options.AllowLargeRead && totalLines > DefaultMaxLines)
            {
                throw new InvalidOperationException(
                    $"File too large ({totalLines} lines). "
                    + $"Default limit is {DefaultMaxLines} lines. "
                    + "Use start_line/end_line, or set allow_large_read=true (optionally with max_lines) to read more.");
            }

            if (options.AllowLargeRead
                && !options.MaxLines.HasValue
                && totalLines > AbsoluteMaxLines)
            {
                throw new InvalidOperationException(
                    $"File too large ({totalLines} lines). "
                    + $"Maximum allowed is {AbsoluteMaxLines} lines without an explicit max_lines value.");
            }
        }

        return new ReadFileResult(resolvedPath, text, md5, sha256);
    }

    public static int ResolveEffectiveMaxLines(ReadFileOptions options)
    {
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

    public async Task<(string NewText, string NewHash)> ReplaceInFileAsync(
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

        var resolvedPath = WorkspaceJail.ResolvePath(_workspaceRoot, path);
        var canonicalContent = await FileTextHelper.ReadCanonicalContentAsync(resolvedPath, cancellationToken);
        FileTextHelper.EnsureHashMatches(originalHash, canonicalContent);

        var normalizedTarget = FileTextHelper.NormalizeLineEndings(targetSnippet);
        var normalizedReplacement = FileTextHelper.NormalizeLineEndings(replacementSnippet);

        var targetIndex = canonicalContent.IndexOf(normalizedTarget, StringComparison.Ordinal);
        if (targetIndex < 0)
        {
            throw new ArgumentException("Target snippet not found in the file. Ensure you copied the exact code block.");
        }

        var updatedText = ReplaceFirst(canonicalContent, normalizedTarget, normalizedReplacement, targetIndex);
        await FileTextHelper.WriteUtf8WithoutBomAsync(resolvedPath, updatedText, cancellationToken);

        var (_, newSha256) = FileTextHelper.ComputeContentHashes(updatedText);
        return (updatedText, newSha256);
    }

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
}
