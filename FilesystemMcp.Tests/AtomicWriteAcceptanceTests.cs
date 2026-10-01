using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-02"), Trait("Status", "Baseline")]
public sealed class AtomicWriteAcceptanceTests
{
    [Theory]
    [InlineData("BeforeLock")]
    [InlineData("DuringWrite")]
    [InlineData("BeforeFlush")]
    [InlineData("AfterTempWrite")]
    [InlineData("BeforeCommit")]
    public async Task PreparationFailuresPreserveOriginalAndCleanOnlyOwnedTemp(string stage)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var foreign = sandbox.Write(".filesystemmcp-unknown.tmp", "leave alone");
        var original = File.ReadAllBytes(path);
        var policy = Policy(sandbox, point => { if (point.ToString() == stage) throw new IOException("injected storage fault"); });
        await Assert.ThrowsAsync<IOException>(() => Replace(policy, new string('x', 150000)));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("leave alone", File.ReadAllText(foreign));
        Assert.Equal([foreign], Directory.GetFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    [Theory]
    [InlineData("DuringWrite")]
    [InlineData("AfterTempWrite")]
    [InlineData("BeforeCommit")]
    public async Task CancellationBeforePublicationPreservesOriginal(string stage)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var original = File.ReadAllBytes(path);
        using var cancelled = new CancellationTokenSource();
        var policy = Policy(sandbox, point => { if (point.ToString() == stage) cancelled.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Replace(policy, new string('x', 150000), cancelled.Token));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Temps(sandbox));
    }

    [Theory]
    [InlineData("FileService")]
    [InlineData("MutationReplace")]
    [InlineData("MutationCreate")]
    [InlineData("Native")]
    public async Task PreCancelledEntryPointsHaveNoFilesystemSideEffects(string entry)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var original = File.ReadAllBytes(path);
        var policy = new PathPolicy(sandbox.Workspace);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var hash = FileTextHelper.ComputeContentHashes("old\ntext").Sha256;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => entry switch
        {
            "FileService" => new FileService(policy).ReplaceInFileAsync("file.txt", "old", "new", hash, cancelled.Token),
            "MutationReplace" => new MutationService(policy).ReplaceInFileAsync("file.txt", "old", "new", hash, cancelled.Token),
            "MutationCreate" => new MutationService(policy).CreateFileAsync("absent/new/file.txt", "new", cancelled.Token),
            _ => NativePath.WriteAsync(policy, "absent/new/file.txt", Path.Combine(sandbox.Workspace, "absent/new/file.txt"), "new", true, cancelled.Token)
        });
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(sandbox.Workspace, "absent")));
        Assert.Empty(Temps(sandbox));
    }

    [Fact]
    public async Task CancellationAtPolicyHookPrecedesMissingParentSideEffects()
    {
        using var sandbox = new Sandbox(); using var cancelled = new CancellationTokenSource();
        var policy = new PathPolicy(sandbox.Workspace) { BeforeWriteCommit = cancelled.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MutationService(policy).CreateFileAsync("absent/new.txt", "created", cancelled.Token));
        Assert.False(Directory.Exists(Path.Combine(sandbox.Workspace, "absent")));
        Assert.Empty(Directory.GetFiles(sandbox.Workspace));
    }

    [Theory]
    [InlineData("AfterCommit")]
    [InlineData("Cleanup")]
    public async Task CommittedWriteIgnoresLateCancellationAndFaults(string stage)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        using var cancelled = new CancellationTokenSource();
        var commits = 0;
        var policy = Policy(sandbox, point =>
        {
            if (point == AtomicWritePoint.AfterCommit) commits++;
            if (point.ToString() == stage) { cancelled.Cancel(); throw new IOException("late diagnostic/cleanup failure"); }
        });
        var result = await Replace(policy, "new", cancelled.Token);
        Assert.Equal("new\r\ntext", File.ReadAllText(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes("new\ntext").Sha256, result.NewHash);
        Assert.Equal(1, commits);
        Assert.Empty(Temps(sandbox));
    }

    [Fact]
    public async Task CompletionLoggerFailureDoesNotChangeToolSuccess()
    {
        using var sandbox = new Sandbox();
        var registry = new ToolRegistry { CompletionLog = (_, _, _, _) => throw new IOException("log sink full") };
        registry.Register(new CreateFileTool(sandbox.Workspace));
        var result = await registry.ExecuteToolAsync("create_file", ServerProcess.Arguments(new { path = "file.txt", content = "created\r\n" }));
        Assert.Equal("success", ServerProcess.JsonDocumentParse(result).GetProperty("status").GetString());
        Assert.Equal(FileTextHelper.ComputeContentHashes("created\n").Sha256, ServerProcess.JsonDocumentParse(result).GetProperty("sha256").GetString());
        Assert.Equal("created\r\n", File.ReadAllText(Path.Combine(sandbox.Workspace, "file.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiskFullDuringWriteOrFlushPreservesOriginal(bool flush)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var original = File.ReadAllBytes(path);
        var policy = new PathPolicy(sandbox.Workspace)
        {
            AtomicWrites = new AtomicWriteDependencies
            {
                WriteChunk = (stream, bytes, offset, count) =>
                {
                    stream.Write(bytes, offset, flush ? count : Math.Min(8, count));
                    if (!flush) throw new IOException("Injected disk full", unchecked((int)0x80070070));
                },
                Flush = stream => { if (flush) throw new IOException("Injected disk full", unchecked((int)0x80070070)); stream.Flush(true); }
            }
        };
        await Assert.ThrowsAsync<IOException>(() => Replace(policy, new string('x', 150000)));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Temps(sandbox));
    }

    [Fact]
    public async Task EncoderFailureDoesNotCreateTempOrTouchOriginal()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var original = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => Replace(new PathPolicy(sandbox.Workspace), "\ud800"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Temps(sandbox));
    }

    [Fact]
    public async Task LeadingBomCreateAndReplacementHashMatchSubsequentRead()
    {
        using var sandbox = new Sandbox();
        var mutation = new MutationService(sandbox.Workspace);
        var created = await mutation.CreateFileAsync("created.txt", "\ufeffcreated\r\n");
        var files = new FileService(sandbox.Workspace);
        var read = await files.ReadFileAsync("created.txt", new());
        Assert.Equal(FileTextHelper.ComputeContentHashes("created\n").Sha256, created.Sha256);
        Assert.Equal(created.Sha256, read.Sha256);
        sandbox.Write("file.txt", "old\r\ntext");
        var changed = await Replace(new PathPolicy(sandbox.Workspace), "\ufeffnew");
        var reread = await files.ReadFileAsync("file.txt", new());
        Assert.Equal(FileTextHelper.ComputeContentHashes("new\ntext").Sha256, changed.NewHash);
        Assert.Equal(changed.NewHash, reread.Sha256);
        var result = await new CreateFileTool(sandbox.Workspace).ExecuteAsync(ServerProcess.Arguments(new { path = "tool.txt", content = "\ufefftool\r\n" }));
        Assert.Equal((await files.ReadFileAsync("tool.txt", new())).Sha256, ServerProcess.JsonDocumentParse(result).GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task FreshCheckRejectsExternalMutationDuringPreparation()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var policy = Policy(sandbox, point => { if (point == AtomicWritePoint.BeforeCommit) File.WriteAllText(path, "external\r\n", Sandbox.Utf8); });
        var error = await Assert.ThrowsAsync<MutationException>(() => Replace(policy, "new"));
        Assert.Equal("hash_conflict", error.Code);
        Assert.Equal("external\r\n", File.ReadAllText(path));
        Assert.Empty(Temps(sandbox));
    }

    [WindowsFact, SupportedOSPlatform("windows")]
    public async Task FreshCheckRejectsChangedAclBeforeCommit()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var policy = Policy(sandbox, point =>
        {
            if (point != AtomicWritePoint.BeforeCommit) return;
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(true, true);
            new FileInfo(path).SetAccessControl(security);
        });
        var error = await Assert.ThrowsAsync<MutationException>(() => Replace(policy, "new"));
        Assert.Equal("hash_conflict", error.Code);
        Assert.Equal("old\r\ntext", File.ReadAllText(path));
        Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
        Assert.Empty(Temps(sandbox));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingWriterFaultOrCancellationDoesNotTouchOtherWritersTemp(bool cancel)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        using var ready = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancelled = new CancellationTokenSource();
        var first = Replace(Policy(sandbox, point =>
        {
            if (point == AtomicWritePoint.BeforeCommit)
            { ready.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Writer release watchdog"); }
        }), "first");
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var ownedByFirst = Assert.Single(Temps(sandbox));
            var second = Replace(Policy(sandbox, point =>
            {
                if (point != AtomicWritePoint.LockContended) return;
                if (cancel) cancelled.Cancel(); else throw new IOException("Lock wait dependency fault");
            }), "second", cancelled.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            else await Assert.ThrowsAsync<IOException>(() => second);
            Assert.Equal("old\r\ntext", File.ReadAllText(path));
            Assert.Equal([ownedByFirst], Temps(sandbox));
        }
        finally { release.Set(); await first; }
        Assert.Equal("first\r\ntext", File.ReadAllText(path));
        Assert.Empty(Temps(sandbox));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task ReplacementsPreserveEncodingBomAndUntouchedMixedEndings(string format)
    {
        using var sandbox = new Sandbox();
        var encoding = format switch
        {
            "utf8bom" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            "utf32be" => new UTF32Encoding(true, true, true),
            _ => (Encoding)new UTF8Encoding(false, true)
        };
        var path = sandbox.Write("file.txt", "prefix\r\nold\rother\nsuffix\r\n", encoding);
        var hash = FileTextHelper.ComputeContentHashes("prefix\nold\nother\nsuffix\n").Sha256;
        var result = await new FileService(sandbox.Workspace).ReplaceInFileAsync("file.txt", "old\nother", "new\nline", hash);
        var expectedText = "prefix\r\nnew\rline\nsuffix\r\n";
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expectedText)).ToArray(), File.ReadAllBytes(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes(FileTextHelper.NormalizeLineEndings(expectedText)).Sha256, result.NewHash);
    }

    [WindowsFact, SupportedOSPlatform("windows")]
    public async Task ProtectedAclAndAttributesArePreserved()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Archive);
        var before = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        var attrs = File.GetAttributes(path);
        await Replace(new PathPolicy(sandbox.Workspace), "new");
        var after = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        Assert.True(after.AreAccessRulesProtected);
        Assert.Equal(before, after.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group));
        Assert.Equal(attrs, File.GetAttributes(path));
    }

    [WindowsFact]
    public async Task ReadOnlyFailureDoesNotChangeAttributesOrBytes()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var original = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var attrs = File.GetAttributes(path);
        try
        {
            var error = await Assert.ThrowsAsync<MutationException>(() => Replace(new PathPolicy(sandbox.Workspace), "new"));
            Assert.Equal("access_denied", error.Code);
            Assert.Equal(attrs, File.GetAttributes(path));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Temps(sandbox));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [WindowsFact, SupportedOSPlatform("windows")]
    public async Task WritableParentCannotBypassOriginalDataWriteAcl()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteData | FileSystemRights.AppendData, AccessControlType.Deny);
        security.AddAccessRule(deny); new FileInfo(path).SetAccessControl(security);
        var before = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        try
        {
            Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            var error = await Assert.ThrowsAsync<MutationException>(() => Replace(new PathPolicy(sandbox.Workspace), "new"));
            Assert.Equal("access_denied", error.Code);
            Assert.Equal("old\r\ntext", File.ReadAllText(path));
            Assert.Equal(before, new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.Empty(Temps(sandbox));
        }
        finally { security.RemoveAccessRuleSpecific(deny); new FileInfo(path).SetAccessControl(security); }
    }

    [UnixFact, SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    public async Task UnixPermissionsAndReadOnlyArePreserved()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await Replace(new PathPolicy(sandbox.Workspace), "new");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        var error = await Assert.ThrowsAsync<MutationException>(() => new FileService(sandbox.Workspace).ReplaceInFileAsync("file.txt", "new", "again",
            FileTextHelper.ComputeContentHashes("new\ntext").Sha256));
        Assert.Equal("access_denied", error.Code);
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(path));
        Assert.Equal("new\r\ntext", File.ReadAllText(path));
    }

    [UnixWritePermissionFact, SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    public async Task UnixOtherWriteBitCannotBypassCurrentOwnersReadOnlyPermission()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var mode = UnixFileMode.UserRead | UnixFileMode.OtherWrite;
        File.SetUnixFileMode(path, mode);
        try
        {
            var error = await Assert.ThrowsAsync<MutationException>(() => Replace(new PathPolicy(sandbox.Workspace), "new"));
            Assert.Equal("access_denied", error.Code);
            Assert.Equal("old\r\ntext", File.ReadAllText(path));
            Assert.Equal(mode, File.GetUnixFileMode(path));
            Assert.Empty(Temps(sandbox));
        }
        finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public async Task ConcurrentObserverSeesOnlyWholeOldOrNewFile()
    {
        using var sandbox = new Sandbox();
        var old = "old\r\n" + new string('a', 250000);
        var next = "new\r\n" + new string('a', 250000);
        var path = sandbox.Write("file.txt", old);
        using var ready = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var samples = new List<string>();
        var observer = Task.Run(() =>
        {
            do
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Sandbox.Utf8);
                samples.Add(reader.ReadToEnd()); ready.Set();
            } while (!finished.IsSet);
        });
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await new FileService(sandbox.Workspace).ReplaceInFileAsync("file.txt", "old", "new", FileTextHelper.ComputeContentHashes(FileTextHelper.NormalizeLineEndings(old)).Sha256);
        }
        finally { finished.Set(); await observer.WaitAsync(TimeSpan.FromSeconds(5)); }
        samples.Add(File.ReadAllText(path));
        Assert.Contains(old, samples); Assert.Contains(next, samples);
        Assert.All(samples, sample => Assert.True(sample == old || sample == next, "Observed partial/truncated file"));
    }

    [Fact]
    public async Task TwoRealProcessesWithSameHashHaveOneSuccessAndOneConflict() => await ProcessRace(false, false);

    [Fact]
    public async Task ConcurrentCreatePublishesOneNewFileOnly() => await ProcessRace(true, false);

    [Fact]
    public async Task ConcurrentCreateWithMissingParentsUsesStableCanonicalLock() => await ProcessRace(true, false, missingParents: true);

    [WindowsFact]
    public async Task TwoProcessesThroughDirectoryAliasShareWriterLock() => await ProcessRace(false, true);

    [ShortNameFact]
    public async Task TwoProcessesUsingEightDotThreeRootShareWriterLock() => await ProcessRace(false, false, true);

    [WindowsFact]
    public async Task TwoProcessesThroughHardLinksSerializeByIdentityAndPreserveOtherLink()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("real/file.txt", "old");
        var secondPath = Path.Combine(sandbox.Workspace, "other.txt");
        var outside = Path.Combine(sandbox.Outside, "hard.txt");
        Assert.True(CreateHardLink(secondPath, path, IntPtr.Zero));
        Assert.True(CreateHardLink(outside, path, IntPtr.Zero));
        await using var firstGate = new ProcessGate(); await using var secondGate = new ProcessGate();
        await using var first = await firstGate.Start(sandbox.Workspace); await using var second = await secondGate.Start(sandbox.Workspace);
        var hash = FileTextHelper.ComputeContentHashes("old").Sha256;
        var one = first.ToolAsync("replace_in_file", new { path = "real/file.txt", target_snippet = "old", replacement_snippet = "first", original_hash = hash });
        await firstGate.At("BeforeLock"); await firstGate.Release(); await firstGate.At("BeforeCommit");
        var two = second.ToolAsync("replace_in_file", new { path = "other.txt", target_snippet = "old", replacement_snippet = "second", original_hash = hash });
        await secondGate.At("BeforeLock"); await secondGate.Release();
        // Different canonical path keys: only the common physical-id lock can
        // produce this deterministic contention event.
        await secondGate.At("LockContended"); await secondGate.Release(); await firstGate.Release();
        Assert.False((await one).GetProperty("result").GetProperty("isError").GetBoolean());
        await secondGate.At("BeforeCommit"); await secondGate.Release();
        Assert.False((await two).GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("first", File.ReadAllText(path)); Assert.Equal("second", File.ReadAllText(secondPath));
        Assert.Equal("old", File.ReadAllText(outside));
    }

    private static async Task ProcessRace(bool create, bool alias, bool shortRoot = false, bool missingParents = false)
    {
        using var sandbox = new Sandbox();
        if (!missingParents) Directory.CreateDirectory(Path.Combine(sandbox.Workspace, "real"));
        if (!create) sandbox.Write("real/file.txt", "old");
        if (alias) await sandbox.JunctionAsync("alias", Path.Combine(sandbox.Workspace, "real"));
        await using var firstGate = new ProcessGate();
        await using var secondGate = new ProcessGate();
        await using var first = await firstGate.Start(sandbox.Workspace);
        await using var second = await secondGate.Start(shortRoot ? ShortNameFactAttribute.GetShortPath(sandbox.Workspace)! : sandbox.Workspace);
        var hash = FileTextHelper.ComputeContentHashes("old").Sha256;
        var firstWrite = create ? first.ToolAsync("create_file", new { path = "real/file.txt", content = "first" })
            : first.ToolAsync("replace_in_file", new { path = "real/file.txt", target_snippet = "old", replacement_snippet = "first", original_hash = hash });
        await firstGate.At("BeforeLock"); await firstGate.Release();
        await firstGate.At("BeforeCommit");
        var secondWrite = create ? second.ToolAsync("create_file", new { path = "real/file.txt", content = "second" })
            : second.ToolAsync("replace_in_file", new { path = (alias ? "alias" : "real") + "/file.txt", target_snippet = "old", replacement_snippet = "second", original_hash = hash });
        await secondGate.At("BeforeLock"); await secondGate.Release();
        await secondGate.At("LockContended"); await secondGate.Release();
        await firstGate.Release();
        var success = await firstWrite; var conflict = await secondWrite;
        Assert.False(success.GetProperty("result").GetProperty("isError").GetBoolean());
        McpAssert.ToolError(conflict, create ? "file_exists" : "hash_conflict");
        Assert.Equal("first", File.ReadAllText(Path.Combine(sandbox.Workspace, "real/file.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(sandbox.Workspace, "real"), ".filesystemmcp-*.tmp"));
    }

    [Fact]
    public async Task AtomicReplacementLeavesExternalHardLinkInodeUntouched()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "old\r\ntext");
        var external = Path.Combine(sandbox.Outside, "hard.txt");
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLink(external, path, IntPtr.Zero));
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) Assert.Equal(0, Link(path, external));
        else throw new PlatformNotSupportedException("Hard-link capability requires a supported OS lane.");
        var oldIdentity = NativePath.GetFileIdentity(path);
        await Replace(new PathPolicy(sandbox.Workspace), "new");
        Assert.Equal("old\r\ntext", File.ReadAllText(external));
        Assert.Equal(oldIdentity, NativePath.GetFileIdentity(external));
        Assert.NotEqual(oldIdentity, NativePath.GetFileIdentity(path));
    }

    private static PathPolicy Policy(Sandbox sandbox, Action<AtomicWritePoint> hook) => new(sandbox.Workspace)
    { AtomicWrites = new AtomicWriteDependencies { Hook = hook } };
    private static Task<(string NewText, string NewHash)> Replace(PathPolicy policy, string next, CancellationToken token = default) =>
        new FileService(policy).ReplaceInFileAsync("file.txt", "old", next, FileTextHelper.ComputeContentHashes("old\ntext").Sha256, token);
    private static string[] Temps(Sandbox sandbox) => Directory.GetFiles(sandbox.Workspace, ".filesystemmcp-*.tmp");

    private sealed class ProcessGate : IAsyncDisposable
    {
        private readonly string _name = "filesystemmcp-tests-" + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        internal ProcessGate() => _pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        internal async Task<ServerProcess> Start(string workspace)
        {
            var connected = _pipe.WaitForConnectionAsync();
            var server = await ServerProcess.StartAsync(workspace, executable: ServerProcess.ProtectedExecutable, environment: new Dictionary<string, string>
            { ["FS_TEST_ATOMIC_PIPE"] = _name, ["FS_TEST_ATOMIC_POINTS"] = "BeforeLock,LockContended,BeforeCommit" });
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            _reader = new StreamReader(_pipe, leaveOpen: true); _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            return server;
        }
        internal async Task At(string point) => Assert.Equal(point, await _reader!.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task Release() => _writer!.WriteLineAsync("continue");
        public async ValueTask DisposeAsync() { _reader?.Dispose(); _writer?.Dispose(); await _pipe.DisposeAsync(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string name, string existing, IntPtr security);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string name);
}
