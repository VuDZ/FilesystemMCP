using System.Security.Cryptography;
using System.Text;

namespace FilesystemMcp;

internal sealed class MutationService
{
    private readonly string _workspaceRoot;

    public MutationService(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("WorkspaceRoot must be provided.", nameof(workspaceRoot));
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public async Task<CreateFileResult> CreateFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = WorkspaceJail.ResolvePath(_workspaceRoot, path);
        if (File.Exists(resolvedPath))
        {
            throw new InvalidOperationException("File already exists. Use replace_in_file instead.");
        }

        var directoryPath = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await using (var stream = new FileStream(
                         resolvedPath,
                         new FileStreamOptions
                         {
                             Access = FileAccess.Write,
                             Mode = FileMode.CreateNew,
                             Share = FileShare.ReadWrite,
                             Options = FileOptions.SequentialScan
                         }))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        var (md5, sha256) = ComputeHashes(content);
        return new CreateFileResult(resolvedPath, md5, sha256);
    }

    public async Task<ReplaceInFileResult> ReplaceInFileAsync(
        string path,
        string targetSnippet,
        string replacementSnippet,
        string originalHash,
        CancellationToken cancellationToken = default)
    {
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
        if (!HashesMatch(originalHash, currentMd5, currentSha256))
        {
            throw new InvalidOperationException(
                "File modified externally. Please use read_file to get the latest state before patching.");
        }

        var index = currentContent.IndexOf(targetSnippet, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("Target snippet not found.");
        }

        var updatedContent = ReplaceFirst(currentContent, targetSnippet, replacementSnippet, index);

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
            await writer.WriteAsync(updatedContent.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        var (updatedMd5, updatedSha256) = ComputeHashes(updatedContent);
        return new ReplaceInFileResult(resolvedPath, updatedMd5, updatedSha256);
    }

    private static bool HashesMatch(string originalHash, string md5, string sha256) =>
        string.Equals(originalHash, md5, StringComparison.OrdinalIgnoreCase)
        || string.Equals(originalHash, sha256, StringComparison.OrdinalIgnoreCase);

    private static string ReplaceFirst(string source, string target, string replacement, int index)
    {
        var builder = new StringBuilder(source.Length - target.Length + replacement.Length);
        builder.Append(source, 0, index);
        builder.Append(replacement);
        builder.Append(source, index + target.Length, source.Length - index - target.Length);
        return builder.ToString();
    }

    private static (string Md5, string Sha256) ComputeHashes(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var md5Bytes = MD5.HashData(bytes);
        var sha256Bytes = SHA256.HashData(bytes);
        return (Convert.ToHexString(md5Bytes), Convert.ToHexString(sha256Bytes));
    }
}
