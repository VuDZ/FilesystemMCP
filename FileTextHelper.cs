using System.Security.Cryptography;
using System.Text;

namespace FilesystemMcp;

internal static class FileTextHelper
{
    private const int BinaryProbeLength = 512;

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
        var streamOptions = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.ReadWrite,
            Options = FileOptions.SequentialScan
        };

        await using var stream = new FileStream(resolvedPath, streamOptions);
        var encoding = await DetectTextEncodingAsync(stream, cancellationToken);
        stream.Position = 0;

        using var reader = new StreamReader(
            stream,
            encoding: encoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        return await reader.ReadToEndAsync(cancellationToken);
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

    public static async Task WriteUtf8WithoutBomAsync(
        string resolvedPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            resolvedPath,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.Create,
                Share = FileShare.ReadWrite,
                Options = FileOptions.SequentialScan
            });

        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
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
            throw new InvalidOperationException(
                "File modified externally. Please use read_file to get the latest state before patching.");
        }
    }

    private static async Task<Encoding> DetectTextEncodingAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var probeBuffer = new byte[BinaryProbeLength];
        var bytesRead = await stream.ReadAsync(probeBuffer.AsMemory(0, BinaryProbeLength), cancellationToken);

        if (bytesRead >= 4
            && probeBuffer[0] == 0xFF
            && probeBuffer[1] == 0xFE
            && probeBuffer[2] == 0x00
            && probeBuffer[3] == 0x00)
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }

        if (bytesRead >= 4
            && probeBuffer[0] == 0x00
            && probeBuffer[1] == 0x00
            && probeBuffer[2] == 0xFE
            && probeBuffer[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        if (bytesRead >= 2 && probeBuffer[0] == 0xFF && probeBuffer[1] == 0xFE)
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }

        if (bytesRead >= 2 && probeBuffer[0] == 0xFE && probeBuffer[1] == 0xFF)
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        if (bytesRead >= 3
            && probeBuffer[0] == 0xEF
            && probeBuffer[1] == 0xBB
            && probeBuffer[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        for (var i = 0; i < bytesRead; i++)
        {
            if (probeBuffer[i] == 0)
            {
                throw new InvalidOperationException("Binary file detected. Cannot read.");
            }
        }

        return Encoding.UTF8;
    }
}
