namespace FilesystemMcp;

/// <summary>
/// One bounded canonical read of a text file: the selected canonical text, the MD5 and
/// SHA-256 of the <em>whole</em> canonical file, and the number of lines the whole file
/// contains. The hashes always describe the complete normalized file, even when only a
/// line range was materialized. FS-10 adds the selection metadata: the actually returned
/// 1-based range (<see cref="StartLine"/>/<see cref="EndLine"/>, null for an empty
/// selection), whether a line cap truncated the answer and whether lines exist past the
/// returned <see cref="EndLine"/>.
/// </summary>
internal sealed record CanonicalContent(
    string Text,
    string Md5,
    string Sha256,
    int TotalLines,
    int? StartLine = null,
    int? EndLine = null,
    bool Truncated = false,
    bool HasMore = false);
