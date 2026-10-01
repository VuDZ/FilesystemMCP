using System.Text;

namespace FilesystemMcp;

internal sealed class MutationService
{
    private readonly PathPolicy _policy;
    private string _workspaceRoot => _policy.Root;

    public MutationService(string workspaceRoot) : this(new PathPolicy(workspaceRoot)) { }

    public MutationService(PathPolicy policy) => _policy = policy;

    public async Task<CreateFileResult> CreateFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var result = await AtomicFileWriter.WriteTextAsync(_policy, path, content, true, cancellationToken);
        return new CreateFileResult(result.Path, result.Md5, result.Sha256);
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

        var result = await AtomicFileWriter.ReplaceAsync(_policy, path, targetSnippet, replacementSnippet, originalHash, cancellationToken);
        return new ReplaceInFileResult(result.Path, result.Md5, result.Sha256);
    }
}