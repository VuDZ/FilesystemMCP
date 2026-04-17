using System.Security.Cryptography;
using System.Text;

namespace FilesystemMcp;

internal sealed class FileService
{
    private const int BinaryProbeLength = 512;
    private const int DefaultMaxLines = 1000;
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
        int? startLine,
        int? endLine,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = WorkspaceJail.ResolvePath(_workspaceRoot, path);

        ValidateLineRange(startLine, endLine);

        var streamOptions = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.ReadWrite,
            Options = FileOptions.SequentialScan
        };

        await using var stream = new FileStream(resolvedPath, streamOptions);
        await EnsureTextFileAsync(stream, cancellationToken);
        stream.Position = 0;

        using var reader = new StreamReader(
            stream,
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        var (text, totalLines) = await ReadRequestedContentAsync(
            reader,
            startLine,
            endLine,
            cancellationToken);

        if (!startLine.HasValue && !endLine.HasValue && totalLines > DefaultMaxLines)
        {
            throw new InvalidOperationException(
                $"File too large ({totalLines} lines). Specify start_line and end_line parameters.");
        }

        var (md5, sha256) = ComputeHashes(text);
        return new ReadFileResult(resolvedPath, text, md5, sha256);
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
        string currentContent;

        await using (var stream = new FileStream(
                         resolvedPath,
                         new FileStreamOptions
                         {
                             Access = FileAccess.Read,
                             Mode = FileMode.Open,
                             Share = FileShare.ReadWrite,
                             Options = FileOptions.SequentialScan
                         }))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            currentContent = await reader.ReadToEndAsync(cancellationToken);
        }

        var (currentMd5, currentSha256) = ComputeHashes(currentContent);
        if (!string.Equals(originalHash, currentMd5, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(originalHash, currentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "File modified externally. Please use read_file to get the latest state before patching.");
        }

        var normalizedFile = NormalizeLineEndings(currentContent);
        var normalizedTarget = NormalizeLineEndings(targetSnippet);
        var normalizedReplacement = NormalizeLineEndings(replacementSnippet);

        var targetIndex = normalizedFile.IndexOf(normalizedTarget, StringComparison.Ordinal);
        if (targetIndex < 0)
        {
            throw new ArgumentException("Target snippet not found in the file. Ensure you copied the exact code block.");
        }

        var updatedText = ReplaceFirst(normalizedFile, normalizedTarget, normalizedReplacement, targetIndex);

        await using (var stream = new FileStream(
                         resolvedPath,
                         new FileStreamOptions
                         {
                             Access = FileAccess.Write,
                             Mode = FileMode.Create,
                             Share = FileShare.ReadWrite,
                             Options = FileOptions.SequentialScan
                         }))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(updatedText.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        var (_, newSha256) = ComputeHashes(updatedText);
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

    private static async Task EnsureTextFileAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var probeBuffer = new byte[BinaryProbeLength];
        var bytesRead = await stream.ReadAsync(probeBuffer.AsMemory(0, BinaryProbeLength), cancellationToken);

        for (var i = 0; i < bytesRead; i++)
        {
            if (probeBuffer[i] == 0)
            {
                throw new InvalidOperationException("Binary file detected. Cannot read.");
            }
        }
    }

    private static async Task<(string Text, int TotalLines)> ReadRequestedContentAsync(
        StreamReader reader,
        int? startLine,
        int? endLine,
        CancellationToken cancellationToken)
    {
        var currentLine = 0;
        var selected = new StringBuilder(capacity: 4096);
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            currentLine++;

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

        return (selected.ToString(), currentLine);
    }

    private static (string Md5, string Sha256) ComputeHashes(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var md5Bytes = MD5.HashData(bytes);
        var sha256Bytes = SHA256.HashData(bytes);

        return (Convert.ToHexString(md5Bytes), Convert.ToHexString(sha256Bytes));
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string ReplaceFirst(string source, string target, string replacement, int index)
    {
        var builder = new StringBuilder(source.Length - target.Length + replacement.Length);
        builder.Append(source, 0, index);
        builder.Append(replacement);
        builder.Append(source, index + target.Length, source.Length - index - target.Length);
        return builder.ToString();
    }
}
