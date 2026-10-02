using System.Security.Cryptography;
using System.Text;

namespace FilesystemMcp;

internal enum AtomicWritePoint { BeforeLock, LockContended, DuringWrite, BeforeFlush, AfterTempWrite, BeforeCommit, AfterCommit, Cleanup }

// Only internal callers (including the protected test host) can supply faults.
internal sealed class AtomicWriteDependencies
{
    internal Action<AtomicWritePoint>? Hook { get; init; }
    internal Action<Stream, byte[], int, int> WriteChunk { get; init; } = (stream, bytes, offset, count) => stream.Write(bytes, offset, count);
    internal Action<FileStream> Flush { get; init; } = stream => stream.Flush(flushToDisk: true);
    internal void Invoke(AtomicWritePoint point) => Hook?.Invoke(point);
}

internal sealed record AtomicWriteResult(string Path, string Text, string Md5, string Sha256);

internal static class AtomicFileWriter
{
    internal static Task<AtomicWriteResult> ReplaceAsync(PathPolicy policy, string requested, string target,
        string replacement, string originalHash, CancellationToken token = default) =>
        RunAsync(policy, requested, false, document =>
        {
            if (document is null) throw new MutationException("file_not_found", "File not found.");
            FileTextHelper.EnsureHashMatches(originalHash, document.Canonical);
            var changed = document.Replace(target, replacement);
            return (changed.Canonical, changed.Bytes);
        }, token);

    internal static Task<AtomicWriteResult> WriteTextAsync(PathPolicy policy, string requested, string text,
        bool create, CancellationToken token = default) =>
        RunAsync(policy, requested, create, _ =>
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(text);
            // A supplied leading U+FEFF becomes a physical UTF-8 BOM. Hash the
            // document subsequent reads will decode, excluding that signature.
            return (TextDocument.Decode(bytes).Canonical, bytes);
        }, token);

    private static Task<AtomicWriteResult> RunAsync(PathPolicy policy, string requested, bool create,
        Func<TextDocument?, (string Text, byte[] Bytes)> prepare, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Mutex ownership is thread-affine. Keep the entire synchronous transaction
        // on one worker thread, rather than carrying a mutex across async awaits.
        return Task.Run(() => Run(policy, requested, create, prepare, token), token);
    }

    private static AtomicWriteResult Run(PathPolicy policy, string requested, bool create,
        Func<TextDocument?, (string Text, byte[] Bytes)> prepare, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var expected = policy.Resolve(requested);
        if (Directory.Exists(expected) || PathPolicy.Comparer.Equals(expected, policy.Root)) throw PathPolicy.Error("path_is_directory");
        var dependencies = policy.AtomicWrites;
        dependencies.Invoke(AtomicWritePoint.BeforeLock);
        token.ThrowIfCancellationRequested();
        using var pathLock = Acquire("path:" + CanonicalLockPath(expected), token, dependencies);
        using var parentAliasLock = Acquire("parent:" + PhysicalParentKey(expected), token, dependencies);
        token.ThrowIfCancellationRequested();
        if (!PathPolicy.Comparer.Equals(policy.Resolve(requested), expected)) throw PathPolicy.Error("path_changed");
        var existed = File.Exists(expected);
        if (create && existed) throw MutationException.Exists();
        var identity = existed ? NativePath.GetFileIdentity(expected) : (NativePath.DirectoryIdentity?)null;
        using var identityLock = identity.HasValue ? Acquire("identity:" + identity.Value, token, dependencies) : null;
        token.ThrowIfCancellationRequested();
        using var parent = new NativePath.AtomicParent(policy, requested, expected, create, token);
        var leaf = Path.GetFileName(expected);
        var original = existed ? ReadBytes(parent, leaf) : null;
        if (identity.HasValue && NativePath.GetFileIdentity(expected) != identity.Value) throw MutationException.Conflict();
        var observed = existed ? Observe(expected) : default;
        var metadata = existed ? ReadMetadata(parent, leaf) : null;
        var document = original is null ? null : TextDocument.Decode(original);
        var prepared = prepare(document);

        // FS-07 R2: the byte budget applies to the RESULT of a write, and the refusal has to
        // happen before the commit point — not after it. A post-commit refusal would report a
        // failure for a file that is already replaced, which is exactly the ambiguity FS-02's
        // commit boundary exists to prevent. The check sits here, before any temp file is
        // created, so a refused write leaves the workspace untouched.
        if (prepared.Bytes.Length > policy.Budget.MaxFileBytes)
        {
            throw new OperationalException(
                ToolErrorCodes.ResourceLimit,
                ToolErrorMessages.ForCode(ToolErrorCodes.ResourceLimit) + " (result is larger than maxFileBytes)");
        }

        var hashes = FileTextHelper.ComputeContentHashes(prepared.Text);
        var result = new AtomicWriteResult(expected, prepared.Text, hashes.Md5, hashes.Sha256);
        token.ThrowIfCancellationRequested();
        var tempName = ".filesystemmcp-" + Guid.NewGuid().ToString("N") + ".tmp";
        using var temp = parent.OpenLeaf(tempName, true);
        var committed = false;
        try
        {
            // SafeFileHandle remains owned here for publication and handle cleanup.
            using (var borrowed = new Microsoft.Win32.SafeHandles.SafeFileHandle(temp.DangerousGetHandle(), false))
            {
                var stream = new FileStream(borrowed, FileAccess.Write, 65536, false);
                try
                {
                    for (var offset = 0; offset < prepared.Bytes.Length; offset += 65536)
                    {
                        token.ThrowIfCancellationRequested();
                        dependencies.WriteChunk(stream, prepared.Bytes, offset, Math.Min(65536, prepared.Bytes.Length - offset));
                        dependencies.Invoke(AtomicWritePoint.DuringWrite);
                    }
                    token.ThrowIfCancellationRequested();
                    dependencies.Invoke(AtomicWritePoint.BeforeFlush);
                    token.ThrowIfCancellationRequested();
                    dependencies.Flush(stream);
                    dependencies.Invoke(AtomicWritePoint.AfterTempWrite);
                    token.ThrowIfCancellationRequested();
                    if (existed)
                    {
                        using var source = parent.OpenLeaf(leaf, false);
                        NativePath.CopyMetadata(source, temp);
                        parent.RequireWriteAccess(leaf, identity!.Value);
                    }
                    dependencies.Invoke(AtomicWritePoint.BeforeCommit);
                    token.ThrowIfCancellationRequested();
                    parent.Verify();
                    if (existed)
                    {
                        if (!File.Exists(expected) || NativePath.GetFileIdentity(expected) != identity!.Value
                            || Observe(expected) != observed || ReadMetadata(parent, leaf) != metadata
                            || !ReadBytes(parent, leaf).AsSpan().SequenceEqual(original))
                            throw MutationException.Conflict();
                    }
                    else if (File.Exists(expected)) throw MutationException.Exists();
                    token.ThrowIfCancellationRequested();
                    parent.Publish(temp, tempName, leaf, create);
                    committed = true;
                    // No token checks or unguarded callbacks beyond the commit boundary.
                    try { dependencies.Invoke(AtomicWritePoint.AfterCommit); } catch { }
                    // The borrowed stream never owns the publication/cleanup handle.
                    if (!OperatingSystem.IsWindows() && create) SafeCleanup();
                    else { try { dependencies.Invoke(AtomicWritePoint.Cleanup); } catch { } }
                }
                finally
                {
                    if (committed) { try { stream.Dispose(); } catch { } }
                    else stream.Dispose();
                }
            }
        }
        finally
        {
            if (!committed) SafeCleanup();
        }
        return result;

        void SafeCleanup()
        {
            try { dependencies.Invoke(AtomicWritePoint.Cleanup); } catch { }
            try { if (!temp.IsClosed) parent.Cleanup(temp, tempName); } catch { }
        }
    }

    private static byte[] ReadBytes(NativePath.AtomicParent parent, string leaf)
    {
        using var handle = parent.OpenLeaf(leaf, false);
        using var stream = new FileStream(handle, FileAccess.Read);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }

    private static string ReadMetadata(NativePath.AtomicParent parent, string leaf)
    {
        using var handle = parent.OpenLeaf(leaf, false);
        return NativePath.MetadataFingerprint(handle);
    }

    private static (long Length, DateTime Modified, FileAttributes Attributes) Observe(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc, info.Attributes);
    }

    private static (string Parent, string Suffix) ExistingParent(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var suffix = Path.GetFileName(path);
        while (!Directory.Exists(parent))
        {
            suffix = Path.Combine(Path.GetFileName(parent), suffix);
            parent = Path.GetDirectoryName(parent) ?? throw PathPolicy.Error("path_changed");
        }
        return (parent, suffix);
    }

    private static string CanonicalLockPath(string path)
    {
        var (parent, suffix) = ExistingParent(path);
        if (OperatingSystem.IsWindows()) parent = NativePath.GetPhysicalDirectoryPath(parent);
        var key = Path.Combine(parent, suffix);
        return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
    }

    private static string PhysicalParentKey(string path)
    {
        var (parent, suffix) = ExistingParent(path);
        var key = NativePath.GetDirectoryIdentity(parent) + ":" + suffix;
        return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
    }

    private static IDisposable Acquire(string key, CancellationToken token, AtomicWriteDependencies dependencies)
    {
        token.ThrowIfCancellationRequested();
        var name = "FilesystemMcp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if (OperatingSystem.IsWindows()) name = "Global\\" + name;
        var mutex = new Mutex(false, name);
        try
        {
            try
            {
                if (mutex.WaitOne(0)) return new MutexLease(mutex);
                dependencies.Invoke(AtomicWritePoint.LockContended);
                if (WaitHandle.WaitAny([mutex, token.WaitHandle]) != 0) token.ThrowIfCancellationRequested();
            }
            catch (AbandonedMutexException) { /* Revalidate state; never recover unknown temps. */ }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch { }
            try { mutex.Dispose(); } catch { }
        }
    }
}
