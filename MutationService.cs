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

        var canonicalContent = FileTextHelper.NormalizeLineEndings(content);
        await FileTextHelper.WriteUtf8WithoutBomAsync(resolvedPath, canonicalContent, cancellationToken);

        var (md5, sha256) = FileTextHelper.ComputeContentHashes(canonicalContent);
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
        var canonicalContent = await FileTextHelper.ReadCanonicalContentAsync(resolvedPath, cancellationToken);
        FileTextHelper.EnsureHashMatches(originalHash, canonicalContent);

        var normalizedTarget = FileTextHelper.NormalizeLineEndings(targetSnippet);
        var normalizedReplacement = FileTextHelper.NormalizeLineEndings(replacementSnippet);

        var index = canonicalContent.IndexOf(normalizedTarget, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("Target snippet not found.");
        }

        var updatedContent = ReplaceFirst(canonicalContent, normalizedTarget, normalizedReplacement, index);
        await FileTextHelper.WriteUtf8WithoutBomAsync(resolvedPath, updatedContent, cancellationToken);

        var (updatedMd5, updatedSha256) = FileTextHelper.ComputeContentHashes(updatedContent);
        return new ReplaceInFileResult(resolvedPath, updatedMd5, updatedSha256);
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
