using System.Security.Cryptography;
using System.Text;

namespace FilesystemMcp;

internal static class FileTextHelper
{

    public static async Task<string> ReadCanonicalContentAsync(
        string resolvedPath,
        CancellationToken cancellationToken = default)
    {
        var rawContent = await ReadRawContentAsync(resolvedPath, cancellationToken);
        return NormalizeLineEndings(rawContent);
    }

    public static async Task<string> ReadRawContentAsync(
        string resolvedPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return (await TextDocument.ParseAsync(stream, cancellationToken)).Text;
    }
    public static (string Text, int TotalLines) ExtractRequestedContent(
        string canonicalContent,
        int? startLine,
        int? endLine,
        int effectiveMaxLines,
        bool isFullFileRead)
    {
        using var reader = new StringReader(canonicalContent);
        var currentLine = 0;
        var selected = new StringBuilder(capacity: 4096);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            currentLine++;

            if (isFullFileRead && currentLine > effectiveMaxLines)
            {
                break;
            }

            if (startLine.HasValue && endLine.HasValue)
            {
                if (currentLine < startLine.Value || currentLine > endLine.Value)
                {
                    continue;
                }
            }

            if (selected.Length > 0)
            {
                selected.Append('\n');
            }

            selected.Append(line);
        }

        if (isFullFileRead)
        {
            while (reader.ReadLine() is not null)
            {
                currentLine++;
            }
        }

        return (selected.ToString(), currentLine);
    }

    public static Task WriteUtf8WithoutBomAsync(string resolvedPath, string content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(resolvedPath);
        return AtomicFileWriter.WriteTextAsync(new PathPolicy(Path.GetDirectoryName(fullPath)!),
            Path.GetFileName(fullPath), content, false, cancellationToken);
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
