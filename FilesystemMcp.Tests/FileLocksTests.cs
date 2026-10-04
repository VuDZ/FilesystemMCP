using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-04")]
public sealed class FileLocksTests
{
    [WindowsFact, Trait("Status", "Baseline")]
    public async Task LockedFileDoesNotDiscardMatchesFromAccessibleFile()
    {
        using var sandbox = new Sandbox();
        var lockedPath = sandbox.Write("locked.txt", "needle");
        sandbox.Write("accessible.txt", "needle");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var response = await server.ToolAsync("search", new { regex = "needle", file_mask = "*.txt" });
        Assert.False(response.TryGetProperty("error", out _), "Search aborted instead of returning partial matches: " + response);
        Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean());
        var payload = ServerProcess.Payload(response);
        Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "accessible.txt");
        Assert.False(payload.GetProperty("truncated").GetBoolean());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.Equal(1, payload.GetProperty("skipped_count").GetInt32());
        var skipped = Assert.Single(payload.GetProperty("skipped").EnumerateArray());
        Assert.Equal("file_locked", skipped.GetProperty("reason").GetString());
        Assert.Equal(skipped.GetProperty("reason").GetString(), skipped.GetProperty("code").GetString());
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task ReadIsAllowedWhenOtherHandleSharesRead()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "readable");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal("readable", (await new FileOperationsService(sandbox.Workspace).ReadFileAsync("file.txt", new())).Text);
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task RenameSucceedsWhileOurReadHandleIsOpen()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "content");
        var renamed = Path.Combine(sandbox.Workspace, "renamed.txt");
        await using var stream = FileTextHelper.OpenReadStream(path);
        // The read contract grants delete sharing, so an editor-style rename must succeed.
        File.Move(path, renamed);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        Assert.Equal("content", await reader.ReadToEndAsync());
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task ReplaceIsFileLockedWhenForeignHandleOnlySharesRead()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = await Assert.ThrowsAsync<MutationException>(() => new FileOperationsService(sandbox.Workspace).ReplaceInFileAsync(
            "file.txt", "original", "changed", FileTextHelper.ComputeContentHashes("original").Sha256));
        Assert.Equal("file_locked", error.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task ReadLockedFileReturnsFileLockedOperationalError()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "content");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        McpAssert.ToolError(await server.ToolAsync("read_file", new { path = "file.txt" }), "file_locked");
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ReadDisappearedFileReturnsFileNotFound()
    {
        using var sandbox = new Sandbox();
        var error = await Assert.ThrowsAsync<MutationException>(() => new FileOperationsService(sandbox.Workspace).ReadFileAsync("gone.txt", new()));
        Assert.Equal("file_not_found", error.Code);
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task DeletionAfterProbeStillSearchesTheSameOpenStream()
    {
        using var sandbox = new Sandbox();
        var victim = sandbox.Write("victim.txt", "needle");
        sandbox.Write("other.txt", "needle");
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            AfterProbe = path => { if (PathPolicy.Comparer.Equals(path, victim)) File.Delete(path); }
        };
        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        // The probe and the search share one handle, so deleting the path mid-read
        // must not abort nor lose the already-open file's matches.
        Assert.Equal(2, payload.GetProperty("matches").GetArrayLength());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
        Assert.False(payload.GetProperty("incomplete").GetBoolean());
    }

    [WindowsFact, Trait("Status", "Baseline"), SupportedOSPlatform("windows")]
    public async Task AclDeniedFileIsReportedAsAccessDeniedAndSearchContinues()
    {
        using var sandbox = new Sandbox();
        var denied = sandbox.Write("denied.txt", "needle");
        sandbox.Write("allowed.txt", "needle");
        var security = new FileInfo(denied).GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        new FileInfo(denied).SetAccessControl(security);
        try
        {
            var readError = await Assert.ThrowsAsync<MutationException>(
                () => new FileOperationsService(sandbox.Workspace).ReadFileAsync("denied.txt", new()));
            Assert.Equal("access_denied", readError.Code);

            var payload = ServerProcess.JsonDocumentParse(await new SearchTool(new PathPolicy(sandbox.Workspace)).ExecuteAsync(
                ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
            Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "allowed.txt");
            Assert.Equal(1, payload.GetProperty("skipped_count").GetInt32());
            Assert.Contains(payload.GetProperty("skipped").EnumerateArray(), item => item.GetProperty("reason").GetString() == "access_denied");
            Assert.True(payload.GetProperty("incomplete").GetBoolean());
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            new FileInfo(denied).SetAccessControl(security);
        }
    }

    [WindowsFact, Trait("Status", "Baseline"), SupportedOSPlatform("windows")]
    public async Task AclDeniedDirectoryIsSkippedBySearchAndReportedByList()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("allowed.txt", "needle");
        var directory = Path.Combine(sandbox.Workspace, "denied-dir");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "hidden.txt"), "needle");
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        new DirectoryInfo(directory).SetAccessControl(security);
        try
        {
            var listError = await Assert.ThrowsAsync<MutationException>(
                () => new ListDirectoryTool(sandbox.Workspace).ExecuteAsync(ServerProcess.Arguments(new { path = "denied-dir" }), default));
            Assert.Equal("access_denied", listError.Code);

            var payload = ServerProcess.JsonDocumentParse(await new SearchTool(new PathPolicy(sandbox.Workspace)).ExecuteAsync(
                ServerProcess.Arguments(new { regex = "needle" }), default));
            Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "allowed.txt");
            Assert.Contains(payload.GetProperty("skipped").EnumerateArray(), item => item.GetProperty("reason").GetString() == "access_denied");
            Assert.True(payload.GetProperty("incomplete").GetBoolean());
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            new DirectoryInfo(directory).SetAccessControl(security);
        }
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task ReadRetriesAndSucceedsAfterLockReleased()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "content");
        var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = new FileOperationsService(sandbox.Workspace);
        // Deterministic: release the foreign handle on the first backoff instead of sleeping.
        service.BeforeReadRetry = _ => held.Dispose();
        try
        {
            var result = await service.ReadFileAsync("file.txt", new());
            Assert.Equal("content", result.Text);
        }
        finally
        {
            held.Dispose();
        }
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task ReadCancellationDuringRetryWaitIsHonored()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "content");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var service = new FileOperationsService(sandbox.Workspace) { BeforeReadRetry = _ => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadFileAsync("file.txt", new(), cancellation.Token));
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task SearchReportsBoundedSkippedDetailsWithCompleteCount()
    {
        using var sandbox = new Sandbox();
        const int lockedCount = 55;
        var handles = new List<FileStream>();
        try
        {
            for (var i = 0; i < lockedCount; i++)
            {
                var path = sandbox.Write($"locked-{i:D2}.txt", "needle");
                handles.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }
            sandbox.Write("accessible.txt", "needle");

            var payload = ServerProcess.JsonDocumentParse(await new SearchTool(new PathPolicy(sandbox.Workspace)).ExecuteAsync(
                ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
            Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "accessible.txt");
            Assert.Equal(lockedCount, payload.GetProperty("skipped_count").GetInt32());
            Assert.Equal(50, payload.GetProperty("skipped").GetArrayLength());
            Assert.True(payload.GetProperty("incomplete").GetBoolean());
            Assert.False(payload.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SearchMarksTruncatedWhenMatchCapIsReached()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("many.txt", string.Join("\n", Enumerable.Repeat("needle", 60)));
        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(new PathPolicy(sandbox.Workspace)).ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        Assert.Equal(50, payload.GetProperty("matches").GetArrayLength());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.False(payload.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public void OnlySharingViolationsAreRetryable()
    {
        Assert.True(FileErrorClassifier.IsSharingViolation(new IOException("locked", unchecked((int)0x80070020))));
        Assert.True(FileErrorClassifier.IsSharingViolation(new IOException("locked", unchecked((int)0x80070021))));
        Assert.False(FileErrorClassifier.IsSharingViolation(new MutationException("hash_conflict", "changed")));
        Assert.False(FileErrorClassifier.IsSharingViolation(new UnauthorizedAccessException("denied")));
        Assert.False(FileErrorClassifier.IsSharingViolation(new FileNotFoundException("missing")));
    }

    // ---- Defect 1: a directory that cannot be probed must be skipped, even behind a narrow mask ----

    [Fact, Trait("Status", "Baseline")]
    public async Task VanishedDirectoryWithNarrowMaskIsSkipped()
    {
        using var sandbox = new Sandbox();
        var directory = Path.Combine(sandbox.Workspace, "vanished");
        Directory.CreateDirectory(directory);
        sandbox.Write("accessible.txt", "needle");
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            BeforeEntryKindProbe = path =>
            {
                if (PathPolicy.Comparer.Equals(path, directory) && Directory.Exists(path)) Directory.Delete(path);
            }
        };
        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "accessible.txt");
        Assert.True(payload.GetProperty("incomplete").GetBoolean(), "vanished directory must make the search incomplete");
        Assert.Contains(payload.GetProperty("skipped").EnumerateArray(), item => item.GetProperty("reason").GetString() == "file_not_found");
    }

    [WindowsFact, Trait("Status", "Baseline"), SupportedOSPlatform("windows")]
    public async Task AclDeniedDirectoryWithNarrowMaskIsSkipped()
    {
        using var sandbox = new Sandbox();
        var directory = Path.Combine(sandbox.Workspace, "denied");
        Directory.CreateDirectory(directory);
        sandbox.Write("accessible.txt", "needle");
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        new DirectoryInfo(directory).SetAccessControl(security);
        try
        {
            var payload = ServerProcess.JsonDocumentParse(await new SearchTool(new PathPolicy(sandbox.Workspace)).ExecuteAsync(
                ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
            Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "accessible.txt");
            Assert.True(payload.GetProperty("incomplete").GetBoolean());
            Assert.Contains(payload.GetProperty("skipped").EnumerateArray(), item => item.GetProperty("reason").GetString() == "access_denied");
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            new DirectoryInfo(directory).SetAccessControl(security);
        }
    }

    // ---- Defect 2: directory probe failures must preserve the native error ----

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task DirectoryVanishedBeforeIdentityIsReportedAsNotFound()
    {
        using var sandbox = new Sandbox();
        var directory = Path.Combine(sandbox.Workspace, "will-vanish");
        Directory.CreateDirectory(directory);
        sandbox.Write("accessible.txt", "needle");
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            BeforeDirectoryIdentity = path =>
            {
                if (PathPolicy.Comparer.Equals(path, directory) && Directory.Exists(path)) Directory.Delete(path);
            }
        };
        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "accessible.txt");
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.Contains(payload.GetProperty("skipped").EnumerateArray(), item => item.GetProperty("reason").GetString() == "file_not_found");
    }

    [WindowsFact, Trait("Status", "Baseline")]
    public async Task LockedDirectoryWithNarrowMaskIsReportedAsFileLocked()
    {
        using var sandbox = new Sandbox();
        var directory = Path.Combine(sandbox.Workspace, "locked-dir");
        Directory.CreateDirectory(directory);
        sandbox.Write("accessible.txt", "needle");
        using var handle = CreateDirectoryHandle(directory);
        Assert.False(handle.IsInvalid, "fixture could not hold the directory open");
        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(new PathPolicy(sandbox.Workspace)).ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));
        Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "accessible.txt");
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.Contains(payload.GetProperty("skipped").EnumerateArray(), item => item.GetProperty("reason").GetString() == "file_locked");
    }

    // ---- Defect 3: a failing workspace root must surface an operational code ----

    [WindowsFact, Trait("Status", "Baseline"), SupportedOSPlatform("windows")]
    public async Task SearchRootAccessDeniedReturnsOperationalError()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "needle");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var security = new DirectoryInfo(sandbox.Workspace).GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        new DirectoryInfo(sandbox.Workspace).SetAccessControl(security);
        try
        {
            McpAssert.ToolError(await server.ToolAsync("search", new { regex = "needle" }), "access_denied");
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            new DirectoryInfo(sandbox.Workspace).SetAccessControl(security);
        }
    }

    // ---- Defect 4: list_directory must not rewrite unrecognized failures ----

    [Fact, Trait("Status", "Baseline")]
    public async Task ListDirectoryOnFileIsNotMislabeledFileNotFound()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "content");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("list_directory", new { path = "file.txt" });
        // ERROR_DIRECTORY (267) is unrecognized by the classifier, so it must stay an
        // internal failure instead of being rewritten to a misleading file_not_found.
        McpAssert.ProtocolError(reply, -32603);
    }

    // ---- Defect 5: Win32 codes must not be applied on Unix ----

    [Fact, Trait("Status", "Baseline")]
    public void UnixPlatformDoesNotClassifyWindowsCodes()
    {
        Assert.Null(FileErrorClassifier.ClassifyForPlatform(new System.ComponentModel.Win32Exception(32), isWindows: false)); // EPIPE
        Assert.Null(FileErrorClassifier.ClassifyForPlatform(new System.ComponentModel.Win32Exception(5), isWindows: false));  // EIO
        Assert.Equal("file_locked", FileErrorClassifier.ClassifyForPlatform(new System.ComponentModel.Win32Exception(32), isWindows: true));
        Assert.Equal("access_denied", FileErrorClassifier.ClassifyForPlatform(new System.ComponentModel.Win32Exception(5), isWindows: true));
    }

    [Fact, Trait("Status", "Baseline")]
    public void UnixErrnoMappingNeverClaimsWindowsOnlyCodes()
    {
        Assert.Null(FileErrorClassifier.ClassifyUnixErrno(32)); // EPIPE
        Assert.Null(FileErrorClassifier.ClassifyUnixErrno(5));  // EIO
        Assert.Null(FileErrorClassifier.ClassifyUnixErrno(11)); // EAGAIN
    }

    [Fact, Trait("Status", "Baseline")]
    public void UnixErrnoAccessAndNotFoundMapOperationally()
    {
        Assert.Equal("access_denied", FileErrorClassifier.ClassifyUnixErrno(13)); // EACCES
        Assert.Equal("access_denied", FileErrorClassifier.ClassifyUnixErrno(1));  // EPERM
        Assert.Equal("file_not_found", FileErrorClassifier.ClassifyUnixErrno(2)); // ENOENT
    }

    // ---- Defect 6: the deadline must bound the wait itself ----

    [Fact, Trait("Status", "Baseline")]
    public async Task RetryWaitIsClampedToTheDeadline()
    {
        var time = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => SharingRetry.RunAsync<object>(
            _ => { attempts++; time.Advance(980); throw SharingViolation(); },
            beforeBackoff: null,
            time,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            isRetryable: _ => true,
            cancellationToken: CancellationToken.None));
        Assert.Equal(2, attempts);
        var wait = Assert.Single(delays);
        Assert.True(wait <= TimeSpan.FromMilliseconds(20), $"wait must be clamped to the remaining budget but was {wait.TotalMilliseconds} ms");
        Assert.True(980 + wait.TotalMilliseconds <= SharingRetry.Deadline.TotalMilliseconds);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task RetryStopsWhenDeadlineAlreadyPassed()
    {
        var time = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => SharingRetry.RunAsync<object>(
            _ => { attempts++; time.Advance(1500); throw SharingViolation(); },
            beforeBackoff: null,
            time,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            isRetryable: _ => true,
            cancellationToken: CancellationToken.None));
        Assert.Equal(1, attempts);
        Assert.Empty(delays);
    }

    private static IOException SharingViolation() => new("locked", unchecked((int)0x80070020));

    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    private static Microsoft.Win32.SafeHandles.SafeFileHandle CreateDirectoryHandle(string path) =>
        CreateFileW(path, GenericRead, 0, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _now;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_now);
        public void Advance(long milliseconds) => _now += milliseconds;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException();
    }
}
