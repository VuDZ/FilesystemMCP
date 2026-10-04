namespace FilesystemMcp;

// ReplaceIndex is the canonical offset of the first match the replace path edited; it is
// null for whole-text writes, which have no edit position (FS-11).
internal sealed record AtomicWriteResult(string Path, string Text, string Md5, string Sha256, int? ReplaceIndex);
