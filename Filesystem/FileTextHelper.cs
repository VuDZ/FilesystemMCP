using System.Security.Cryptography;
using System.Text;

namespace FilesystemMcp;

internal static class FileTextHelper
{
    public static Task<string> ReadCanonicalContentAsync(
        string resolvedPath,
        CancellationToken cancellationToken = default) =>
        ReadCanonicalContentAsync(resolvedPath, beforeRetry: null, cancellationToken);

    internal static async Task<string> ReadCanonicalContentAsync(
        string resolvedPath,
        Action<int>? beforeRetry,
        CancellationToken cancellationToken = default)
    {
        var rawContent = await ReadRawContentAsync(resolvedPath, beforeRetry, cancellationToken);
        return NormalizeLineEndings(rawContent);
    }

    public static Task<string> ReadRawContentAsync(
        string resolvedPath,
        CancellationToken cancellationToken = default) =>
        ReadRawContentAsync(resolvedPath, beforeRetry: null, cancellationToken);

    internal static async Task<string> ReadRawContentAsync(
        string resolvedPath,
        Action<int>? beforeRetry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SharingRetry.RunAsync(
                async token =>
                {
                    await using var stream = OpenReadStream(resolvedPath);
                    return (await TextDocument.ParseAsync(stream, token)).Text;
                },
                beforeRetry,
                cancellationToken);
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
    /// The single read-open contract used by read_file and search: another process
    /// may still write, rename or delete the file while we hold this handle. Write
    /// sharing is governed by the FS-02 strategy and is deliberately not reused here.
    /// </summary>
    internal static FileStream OpenReadStream(string resolvedPath) =>
        new(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// FS-10 reference selection over an already canonical (LF) text: selected lines are
    /// joined with one LF, a leading blank selected line survives, and the LF that closed
    /// the last selected line in the source is returned too — so the answer is an exact
    /// substring of the canonical text and a terminal newline survives a full-file read.
    /// Keeps <see cref="FileContentReader"/>'s streaming sink and this helper in lockstep.
    /// </summary>
    public static (string Text, int TotalLines) ExtractRequestedContent(
        string canonicalContent,
        int? startLine,
        int? endLine,
        int effectiveMaxLines,
        bool isFullFileRead)
    {
        var selected = new StringBuilder(capacity: 4096);
        var hasSelection = false;
        var lastClosedByDelimiter = false;
        var currentLine = 0;
        var index = 0;

        while (index < canonicalContent.Length)
        {
            var delimiter = canonicalContent.IndexOf('\n', index);
            string line;
            bool closedByDelimiter;
            if (delimiter < 0)
            {
                line = canonicalContent[index..];
                closedByDelimiter = false;
                index = canonicalContent.Length;
            }
            else
            {
                line = canonicalContent[index..delimiter];
                closedByDelimiter = true;
                index = delimiter + 1;
            }

            currentLine++;
            var materialize = isFullFileRead
                ? currentLine <= effectiveMaxLines
                : currentLine >= (startLine ?? 1) && currentLine <= (endLine ?? int.MaxValue);
            if (!materialize)
            {
                continue;
            }

            if (hasSelection)
            {
                selected.Append('\n');
            }

            selected.Append(line);
            hasSelection = true;
            lastClosedByDelimiter = closedByDelimiter;
        }

        if (hasSelection && lastClosedByDelimiter)
        {
            selected.Append('\n');
        }

        return (selected.ToString(), currentLine);
    }

    public static async Task WriteUtf8WithoutBomAsync(
        string resolvedPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(resolvedPath);
        try
        {
            await AtomicFileWriter.WriteTextAsync(new PathPolicy(Path.GetDirectoryName(fullPath)!),
                Path.GetFileName(fullPath), content, false, cancellationToken);
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

    public static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    public static (string Md5, string Sha256) ComputeContentHashes(string canonicalContent)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalContent);
        var md5Bytes = MD5.HashData(bytes);
        var sha256Bytes = SHA256.HashData(bytes);

        return (Convert.ToHexString(md5Bytes), Convert.ToHexString(sha256Bytes));
    }

    public static bool HashesMatch(string originalHash, string md5, string sha256) =>
        string.Equals(originalHash, md5, StringComparison.OrdinalIgnoreCase)
        || string.Equals(originalHash, sha256, StringComparison.OrdinalIgnoreCase);

    public static void EnsureHashMatches(string originalHash, string canonicalContent)
    {
        var (md5, sha256) = ComputeContentHashes(canonicalContent);
        if (!HashesMatch(originalHash, md5, sha256))
        {
            throw MutationException.Conflict();
        }
    }
}
