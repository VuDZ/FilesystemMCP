using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-01"), Trait("Status", "Baseline")]
public sealed class WorkspaceLinksAcceptanceTests
{
    [Fact]
    public async Task GuardedCreateSupportsMissingParents()
    {
        using var sandbox = new Sandbox();
        await new MutationService(sandbox.Workspace).CreateFileAsync("new/deep/file.txt", "inside");
        Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "new/deep/file.txt")));
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task RootAndExistingDirectoryCannotBecomeAnUnexpectedFile(bool allow)
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sandbox.Workspace, "existing"));
        var policy = new PathPolicy(sandbox.Workspace, new(allow));
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=" + allow.ToString().ToLowerInvariant()]);
        foreach (var path in new[] { ".", "existing" })
        {
            McpAssert.ToolError(await server.ToolAsync("create_file", new { path, content = "unexpected" }), "path_is_directory");
            var legacy = await server.CallAsync("create_file", new { path, content = "unexpected" });
            Assert.Equal("path_is_directory", legacy.GetProperty("error").GetProperty("message").GetString());
            Assert.Equal("path_is_directory", (await Assert.ThrowsAsync<PathPolicyException>(() =>
                new MutationService(policy).CreateFileAsync(path, "unexpected"))).Code);
        }
        Assert.Equal("path_is_directory", (await Assert.ThrowsAsync<PathPolicyException>(() =>
            NativePath.WriteAsync(policy, ".", policy.Root, "unexpected", create: true))).Code);
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, Path.GetFileName(sandbox.Workspace))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(sandbox.Workspace, "existing")));
        Assert.Single(Directory.EnumerateFileSystemEntries(sandbox.Workspace));
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task DirectOutsidePathsRemainRejectedForBothModes(bool allow)
    {
        using var sandbox = new Sandbox();
        var outside = Path.Combine(sandbox.Outside, "secret.txt");
        await File.WriteAllTextAsync(outside, "outside");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=" + allow.ToString().ToLowerInvariant()]);
        foreach (var path in new[] { outside, "../outside/secret.txt", "../outside/../" + Path.GetFileName(sandbox.Workspace) + "/file.txt" })
        {
            McpAssert.ToolError(await server.ToolAsync("read_file", new { path }), "path_outside_workspace");
            McpAssert.ToolError(await server.ToolAsync("create_file", new { path, content = "changed" }), "path_outside_workspace");
            McpAssert.ToolError(await server.ToolAsync("replace_in_file", new { path, target_snippet = "outside", replacement_snippet = "changed", original_hash = FileTextHelper.ComputeContentHashes("outside").Sha256 }), "path_outside_workspace");
        }
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
    }

    [WindowsFact]
    public async Task NestedDriveComponentsCannotResetTheWorkspacePathInEitherMode()
    {
        using var sandbox = new Sandbox();
        var outside = Path.Combine(sandbox.Outside, "secret.txt");
        await File.WriteAllTextAsync(outside, "outside");
        var drive = Path.GetPathRoot(sandbox.Root)![..2];
        var requested = "dummy/" + drive + "outside/secret.txt";
        foreach (var allow in new[] { false, true })
        {
            var policy = new PathPolicy(sandbox.Workspace, new(allow));
            Assert.Equal("path_outside_workspace", Assert.Throws<PathPolicyException>(() => policy.Resolve(requested)).Code);
            Assert.True(Path.IsPathFullyQualified(policy.Resolve("new/file.txt")));
            await using var server = await ServerProcess.StartAsync(sandbox.Workspace,
                ["--allowSymLinks=" + allow.ToString().ToLowerInvariant()], workingDirectory: sandbox.Root);
            McpAssert.ToolError(await server.ToolAsync("read_file", new { path = requested }), "path_outside_workspace");
            McpAssert.ToolError(await server.ToolAsync("create_file", new { path = requested, content = "changed" }), "path_outside_workspace");
            foreach (var method in new[] { "read_file", "create_file", "replace_in_file", "list_directory", "append_to_file" })
            {
                var reply = await server.CallAsync(method, new { path = requested, content = "changed", target_snippet = "outside", replacement_snippet = "changed", original_hash = FileTextHelper.ComputeContentHashes("outside").Sha256 });
                Assert.Equal(-32001, reply.GetProperty("error").GetProperty("code").GetInt32());
                Assert.Equal("path_outside_workspace", reply.GetProperty("error").GetProperty("message").GetString());
            }
        }
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Workspace));
    }

    [UnixFact]
    public async Task UnixColonNamesRemainOrdinaryWorkspaceComponents()
    {
        using var sandbox = new Sandbox();
        foreach (var allow in new[] { false, true })
        {
            var path = "dummy/C:ordinary/file" + allow + ".txt";
            var policy = new PathPolicy(sandbox.Workspace, new(allow));
            Assert.True(Path.IsPathFullyQualified(policy.Resolve(path)));
            await new MutationService(policy).CreateFileAsync(path, "inside");
            Assert.Equal("inside", (await new FileService(policy).ReadFileAsync(path, new())).Text);
        }
    }

    [ShortNameFact]
    public async Task SearchDoesNotRevisitAnEightDotThreeAliasOfTheRoot()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("file.txt", "needle");
        var shortPath = ShortNameFactAttribute.GetShortPath(sandbox.Workspace)!;
        Assert.False(string.Equals(sandbox.Workspace, shortPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(NativePath.GetDirectoryIdentity(sandbox.Workspace), NativePath.GetDirectoryIdentity(shortPath));
        await sandbox.JunctionAsync("back", shortPath);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var payload = ServerProcess.Payload(await server.ToolAsync("search", new { regex = "needle" }));
        Assert.Equal("file.txt", Assert.Single(payload.GetProperty("matches").EnumerateArray()).GetProperty("path").GetString());
        var skipped = Assert.Single(payload.GetProperty("skipped").EnumerateArray());
        Assert.Equal("back", skipped.GetProperty("path").GetString());
        Assert.Equal("already_visited", skipped.GetProperty("code").GetString());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        var create = await server.ToolAsync("create_file", new { path = "back/created.txt", content = "inside" });
        Assert.False(create.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "created.txt")));
        var replace = await server.ToolAsync("replace_in_file", new { path = "back/created.txt", target_snippet = "inside", replacement_snippet = "updated", original_hash = FileTextHelper.ComputeContentHashes("inside").Sha256 });
        Assert.False(replace.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "created.txt")));
        await File.WriteAllTextAsync(Path.Combine(sandbox.Outside, "file.txt"), "outside");
        await sandbox.JunctionAsync("external-short", ShortNameFactAttribute.GetShortPath(sandbox.Outside)!);
        var externalCreate = await server.ToolAsync("create_file", new { path = "external-short/deep/new.txt", content = "created" });
        Assert.False(externalCreate.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("created", await File.ReadAllTextAsync(Path.Combine(sandbox.Outside, "deep/new.txt")));
        var externalReplace = await server.ToolAsync("replace_in_file", new { path = "external-short/file.txt", target_snippet = "outside", replacement_snippet = "updated", original_hash = FileTextHelper.ComputeContentHashes("outside").Sha256 });
        Assert.False(externalReplace.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(sandbox.Outside, "file.txt")));
    }

    [WindowsFact]
    public async Task DefaultTrueUsesExternalLinksForReadCreateReplaceListAndSearch()
    {
        using var sandbox = new Sandbox();
        var outside = Path.Combine(sandbox.Outside, "secret.txt");
        await File.WriteAllTextAsync(outside, "needle old");
        await sandbox.JunctionAsync("external", sandbox.Outside);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var read = ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "external/secret.txt" }));
        Assert.Equal("needle old", read.GetProperty("text").GetString());
        var create = await server.ToolAsync("create_file", new { path = "external/new/deep.txt", content = "needle created" });
        Assert.False(create.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("needle created", await File.ReadAllTextAsync(Path.Combine(sandbox.Outside, "new/deep.txt")));
        var replace = await server.ToolAsync("replace_in_file", new { path = "external/secret.txt", target_snippet = "old", replacement_snippet = "new", original_hash = read.GetProperty("sha256").GetString() });
        Assert.False(replace.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("needle new", await File.ReadAllTextAsync(outside));
        Assert.Equal(2, ServerProcess.Payload(await server.ToolAsync("list_directory", new { path = "external" })).GetProperty("entries").GetArrayLength());
        var search = ServerProcess.Payload(await server.ToolAsync("search", new { regex = "needle" }));
        Assert.False(search.GetProperty("incomplete").GetBoolean());
        Assert.Equal(2, search.GetProperty("matches").GetArrayLength());
        foreach (var match in search.GetProperty("matches").EnumerateArray())
        {
            var path = match.GetProperty("path").GetString()!;
            Assert.StartsWith("external" + Path.DirectorySeparatorChar, path);
            Assert.Contains("needle", ServerProcess.Payload(await server.ToolAsync("read_file", new { path })).GetProperty("text").GetString());
        }
        await File.WriteAllTextAsync(Path.Combine(sandbox.Root, "parent.txt"), "physical parent");
        sandbox.Write("parent.txt", "logical parent");
        Assert.Equal("physical parent", ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "external/../parent.txt" })).GetProperty("text").GetString());
        McpAssert.ToolError(await server.ToolAsync("read_file", new { path = "external/../../parent.txt" }), "path_outside_workspace");
    }

    [UnixFact]
    public async Task ReplacedPinnedParentIdentityPreventsWritingMovedOutsideFile()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("parent/file.txt", "original");
        var parent = Path.Combine(sandbox.Workspace, "parent");
        var moved = Path.Combine(sandbox.Outside, "moved");
        var policy = new PathPolicy(sandbox.Workspace, new(false));
        var invoked = false;
        policy.AfterWriteParentsPinned = () =>
        {
            invoked = true;
            Directory.Move(parent, moved);
            Directory.CreateDirectory(parent);
            File.WriteAllText(Path.Combine(parent, "file.txt"), "replacement");
        };
        var error = await Assert.ThrowsAsync<PathPolicyException>(() => new MutationService(policy).ReplaceInFileAsync(
            "parent/file.txt", "original", "changed", FileTextHelper.ComputeContentHashes("original").Sha256));
        Assert.True(invoked);
        Assert.Equal("path_changed", error.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(moved, "file.txt")));
        Assert.Equal("replacement", await File.ReadAllTextAsync(Path.Combine(parent, "file.txt")));
    }

    [Theory]
    [InlineData("--allowSymLinks")]
    [InlineData("--allowSymLinks=")]
    [InlineData("--allowSymLinks=yes")]
    [InlineData("--allowSymLinks=True")]
    [InlineData("--unknown=false")]
    [InlineData("extra-workspace")]
    public async Task InvalidStartupOptionsRejectWithoutStdout(string option)
    {
        using var sandbox = new Sandbox();
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse([sandbox.Workspace, option]));
        var result = await ProcessRunner.RunAsync("dotnet", [ServerProcess.ProtectedExecutable, sandbox.Workspace, option]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("Startup failed", result.Stderr);
    }

    [Fact]
    public async Task DuplicateStartupOptionIsRejected()
    {
        using var sandbox = new Sandbox();
        var result = await ProcessRunner.RunAsync("dotnet", [ServerProcess.ProtectedExecutable, sandbox.Workspace, "--allowSymLinks=true", "--allowSymLinks=false"]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("Startup failed", result.Stderr);
        Assert.True(ServerOptions.Parse([sandbox.Workspace]).Options.AllowSymLinks);
        Assert.False(ServerOptions.Parse([sandbox.Workspace, "--allowSymLinks=false"]).Options.AllowSymLinks);
        Assert.True(ServerOptions.Parse([sandbox.Workspace, "--allowSymLinks=true"]).Options.AllowSymLinks);
    }

    [WindowsFact]
    public async Task ListShowsExternalJunctionNameWithoutFollowingTarget()
    {
        using var sandbox = new Sandbox();
        await sandbox.JunctionAsync("external", sandbox.Outside);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=false"]);
        var payload = ServerProcess.Payload(await server.ToolAsync("list_directory", new { path = "." }));
        Assert.Equal("link", Assert.Single(payload.GetProperty("entries").EnumerateArray()).GetProperty("type").GetString());
        McpAssert.ToolError(await server.ToolAsync("list_directory", new { path = "external" }), "symlink_not_allowed");
    }

    [WindowsFact]
    public async Task SearchSkipsLinksAndReportsIncomplete()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("inside.txt", "needle");
        await File.WriteAllTextAsync(Path.Combine(sandbox.Outside, "secret.txt"), "needle");
        await sandbox.JunctionAsync("external", sandbox.Outside);
        foreach (var allow in new[] { false, true })
        {
            await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=" + allow.ToString().ToLowerInvariant()]);
            var payload = ServerProcess.Payload(await server.ToolAsync("search", new { regex = "needle" }));
            Assert.Equal(allow ? 2 : 1, payload.GetProperty("matches").GetArrayLength());
            Assert.Equal(!allow, payload.GetProperty("incomplete").GetBoolean());
            if (allow) Assert.Empty(payload.GetProperty("skipped").EnumerateArray());
            else Assert.Equal("symlink_not_allowed", Assert.Single(payload.GetProperty("skipped").EnumerateArray()).GetProperty("code").GetString());
        }
    }

    [WindowsFact]
    public async Task LinkedParentCreateAndInternalChainStayInside()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("real/nested/file.txt", "inside");
        await sandbox.JunctionAsync("first", Path.Combine(sandbox.Workspace, "real"));
        await sandbox.JunctionAsync("second", Path.Combine(sandbox.Workspace, "first"));
        var policy = new PathPolicy(sandbox.Workspace, new(true));
        await new MutationService(policy).CreateFileAsync("second/nested/deeper/new.txt", "created");
        Assert.Equal("created", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "real/nested/deeper/new.txt")));
        await new FileService(policy).ReplaceInFileAsync("second/nested/file.txt", "inside", "changed", FileTextHelper.ComputeContentHashes("inside").Sha256);
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "real/nested/file.txt")));
        Assert.Equal("symlink_not_allowed", Assert.Throws<PathPolicyException>(() => new PathPolicy(sandbox.Workspace, new(false)).Resolve("second/nested/new.txt")).Code);
    }

    [WindowsFact]
    public async Task ParentDotDotIsAppliedAfterResolvingLink()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("real/nested/x.txt", "nested");
        sandbox.Write("real/x.txt", "physical parent");
        sandbox.Write("x.txt", "logical parent");
        await sandbox.JunctionAsync("link", Path.Combine(sandbox.Workspace, "real/nested"));
        var read = await new FileService(new PathPolicy(sandbox.Workspace, new(true))).ReadFileAsync("link/../x.txt", new());
        Assert.Equal("physical parent", read.Text);
    }

    [WindowsFact]
    public async Task LinkedRootFalseRejectsAndTrueDefinesPhysicalJail()
    {
        using var sandbox = new Sandbox();
        await File.WriteAllTextAsync(Path.Combine(sandbox.Outside, "inside.txt"), "new root");
        await sandbox.JunctionAsync("root", sandbox.Outside);
        var root = Path.Combine(sandbox.Workspace, "root");
        var denied = await ProcessRunner.RunAsync("dotnet", [ServerProcess.ProtectedExecutable, root, "--allowSymLinks=false"]);
        Assert.NotEqual(0, denied.ExitCode);
        Assert.Equal("", denied.Stdout);
        Assert.Contains("symlink_not_allowed", denied.Stderr);
        await using var server = await ServerProcess.StartAsync(root, ["--allowSymLinks=true"]);
        Assert.Equal("new root", ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "inside.txt" })).GetProperty("text").GetString());
        McpAssert.ToolError(await server.ToolAsync("read_file", new { path = "../x.txt" }), "path_outside_workspace");
    }

    [WindowsFact]
    public async Task LinkedAncestorAboveRootIsCanonicalizedUnderFalse()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sandbox.Outside, "child"));
        await sandbox.JunctionAsync("ancestor", sandbox.Outside);
        var policy = new PathPolicy(Path.Combine(sandbox.Workspace, "ancestor/child"), new(false));
        Assert.Equal(Path.Combine(sandbox.Outside, "child"), policy.Root);
        await new MutationService(policy).CreateFileAsync("new.txt", "safe");
        Assert.Equal("safe", await File.ReadAllTextAsync(Path.Combine(sandbox.Outside, "child/new.txt")));
    }

    [WindowsFact]
    public async Task SearchVisitsPhysicalDirectoriesOnceIncludingCycleToRoot()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("real/file.txt", "needle");
        await sandbox.JunctionAsync("alias", Path.Combine(sandbox.Workspace, "real"));
        await sandbox.JunctionAsync("real/back", sandbox.Workspace);
        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(new PathPolicy(sandbox.Workspace, new(true)))
            .ExecuteAsync(ServerProcess.Arguments(new { regex = "needle" })).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.All(payload.GetProperty("skipped").EnumerateArray(), item => Assert.Equal("already_visited", item.GetProperty("code").GetString()));
    }

    [WindowsFact]
    public async Task DanglingJunctionReturnsExplicitErrorAndSearchContinues()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("good.txt", "needle");
        Directory.CreateDirectory(Path.Combine(sandbox.Workspace, "target"));
        await sandbox.JunctionAsync("dangling", Path.Combine(sandbox.Workspace, "target"));
        Directory.Delete(Path.Combine(sandbox.Workspace, "target"));
        var policy = new PathPolicy(sandbox.Workspace, new(true));
        Assert.Equal("symlink_dangling", Assert.Throws<PathPolicyException>(() => policy.Resolve("dangling/new.txt")).Code);
        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(policy).ExecuteAsync(ServerProcess.Arguments(new { regex = "needle" })));
        Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal("symlink_dangling", Assert.Single(payload.GetProperty("skipped").EnumerateArray()).GetProperty("code").GetString());
    }

    [WindowsFact]
    public async Task ParentSwapBeforeCommitNeverChangesOutsideBytes()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("parent/file.txt", "inside");
        var outside = Path.Combine(sandbox.Outside, "file.txt");
        await File.WriteAllTextAsync(outside, "outside");
        await sandbox.JunctionAsync("redirect", sandbox.Outside);
        var policy = new PathPolicy(sandbox.Workspace, new(false));
        policy.BeforeWriteCommit = () =>
        {
            Directory.Move(Path.Combine(sandbox.Workspace, "parent"), Path.Combine(sandbox.Workspace, "old-parent"));
            Directory.Move(Path.Combine(sandbox.Workspace, "redirect"), Path.Combine(sandbox.Workspace, "parent"));
        };
        var exception = await Assert.ThrowsAsync<PathPolicyException>(() => new MutationService(policy).ReplaceInFileAsync("parent/file.txt", "inside", "changed", FileTextHelper.ComputeContentHashes("inside").Sha256));
        Assert.Equal("symlink_not_allowed", exception.Code);
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
        Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "old-parent/file.txt")));
    }

    [WindowsFact]
    public async Task PinnedParentRejectsRenameDuringActualCommit()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("parent/file.txt", "inside");
        var outside = Path.Combine(sandbox.Outside, "file.txt");
        await File.WriteAllTextAsync(outside, "outside");
        var policy = new PathPolicy(sandbox.Workspace, new(false));
        var invoked = false;
        policy.AfterWriteParentsPinned = () =>
        {
            invoked = true;
            Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(sandbox.Workspace, "parent"), Path.Combine(sandbox.Workspace, "moved")));
        };
        await new MutationService(policy).ReplaceInFileAsync("parent/file.txt", "inside", "changed", FileTextHelper.ComputeContentHashes("inside").Sha256);
        Assert.True(invoked);
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "parent/file.txt")));
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public void UnknownReparseTagsFailClosed()
    {
        Assert.Equal("unsupported_reparse_point", Assert.Throws<PathPolicyException>(() => NativePath.ValidateReparseTag(0x80000042)).Code);
        NativePath.ValidateReparseTag(0xA000000C);
        NativePath.ValidateReparseTag(0xA0000003);
    }

    [WindowsFact]
    public async Task LegacyPathCallsUseTheSamePolicyAndCode()
    {
        using var sandbox = new Sandbox();
        await sandbox.JunctionAsync("external", sandbox.Outside);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--allowSymLinks=false"]);
        foreach (var method in new[] { "read_file", "create_file", "replace_in_file", "list_directory", "append_to_file" })
        {
            var reply = await server.CallAsync(method, new { path = "external/file.txt", content = "changed", target_snippet = "old", replacement_snippet = "new", original_hash = "hash" });
            Assert.Equal(-32001, reply.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal("symlink_not_allowed", reply.GetProperty("error").GetProperty("message").GetString());
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Outside));
    }

    [SymlinkFact]
    public async Task FileSymlinksEnforceBothPoliciesAndMutations()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("inside.txt", "inside");
        var outside = Path.Combine(sandbox.Outside, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside");
        sandbox.Symlink("internal.txt", "inside.txt");
        sandbox.Symlink("external.txt", outside);
        var denied = new PathPolicy(sandbox.Workspace, new(false));
        var error = await Assert.ThrowsAsync<PathPolicyException>(() => new MutationService(denied).ReplaceInFileAsync("external.txt", "outside", "changed", FileTextHelper.ComputeContentHashes("outside").Sha256));
        Assert.Equal("symlink_not_allowed", error.Code);
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
        Assert.Equal("symlink_not_allowed", Assert.Throws<PathPolicyException>(() => denied.Resolve("internal.txt")).Code);
        var defaults = new PathPolicy(sandbox.Workspace);
        Assert.Equal("outside", (await new FileService(defaults).ReadFileAsync("external.txt", new())).Text);
        await new MutationService(defaults).ReplaceInFileAsync("external.txt", "outside", "changed", FileTextHelper.ComputeContentHashes("outside").Sha256);
        Assert.Equal("changed", await File.ReadAllTextAsync(outside));
        var allowed = new PathPolicy(sandbox.Workspace, new(true));
        Assert.Equal("inside", (await new FileService(allowed).ReadFileAsync("internal.txt", new())).Text);
        await new MutationService(allowed).ReplaceInFileAsync("internal.txt", "inside", "changed", FileTextHelper.ComputeContentHashes("inside").Sha256);
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "inside.txt")));
        var list = ServerProcess.JsonDocumentParse(await new ListDirectoryTool(allowed).ExecuteAsync(ServerProcess.Arguments(new { path = "." })));
        Assert.Equal(2, list.GetProperty("entries").EnumerateArray().Count(item => item.GetProperty("type").GetString() == "link"));
    }

    [SymlinkFact]
    public async Task DanglingAndSelfCyclesReturnExplicitErrorsAndSearchContinues()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("good.txt", "needle");
        sandbox.Symlink("dangling", "missing");
        sandbox.Symlink("self", "self");
        sandbox.Symlink("a", "b");
        sandbox.Symlink("b", "a");
        var policy = new PathPolicy(sandbox.Workspace, new(true));
        Assert.Equal("symlink_dangling", Assert.Throws<PathPolicyException>(() => policy.Resolve("dangling")).Code);
        Assert.Equal("symlink_cycle", Assert.Throws<PathPolicyException>(() => policy.Resolve("self")).Code);
        Assert.Equal("symlink_cycle", Assert.Throws<PathPolicyException>(() => policy.Resolve("a")).Code);
        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(policy).ExecuteAsync(ServerProcess.Arguments(new { regex = "needle" })).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal(4, payload.GetProperty("skipped").GetArrayLength());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
    }

    [SymlinkFact]
    public async Task PortableDirectoryLinksChainsRootAndParentCreate()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("real/nested/file.txt", "inside");
        sandbox.Symlink("first", "real/nested", true);
        sandbox.Symlink("second", "first", true);
        sandbox.Symlink("real/nested/back", "../..", true);
        var policy = new PathPolicy(sandbox.Workspace, new(true));
        await new MutationService(policy).CreateFileAsync("second/deep/новый.txt", "needle");
        Assert.Equal("needle", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "real/nested/deep/новый.txt")));
        Assert.Equal(Path.Combine(sandbox.Workspace, "real/new.txt"), policy.Resolve("second/../new.txt"));
        Assert.Throws<PathPolicyException>(() => new PathPolicy(Path.Combine(sandbox.Workspace, "first"), new(false)));
        Assert.Equal(Path.Combine(sandbox.Workspace, "real/nested"), new PathPolicy(Path.Combine(sandbox.Workspace, "first"), new(true)).Root);
        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(policy).ExecuteAsync(ServerProcess.Arguments(new { regex = "needle" })).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Contains(payload.GetProperty("skipped").EnumerateArray(), item => item.GetProperty("code").GetString() == "already_visited");
    }

    [SymlinkFact]
    public async Task PortableParentSwapBeforeCreatePreservesOutside()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("parent/old.txt", "inside");
        var outside = Path.Combine(sandbox.Outside, "new.txt");
        await File.WriteAllTextAsync(outside, "outside");
        sandbox.Symlink("redirect", sandbox.Outside, true);
        var policy = new PathPolicy(sandbox.Workspace, new(true));
        policy.BeforeWriteCommit = () =>
        {
            Directory.Move(Path.Combine(sandbox.Workspace, "parent"), Path.Combine(sandbox.Workspace, "old-parent"));
            Directory.Move(Path.Combine(sandbox.Workspace, "redirect"), Path.Combine(sandbox.Workspace, "parent"));
        };
        await Assert.ThrowsAsync<PathPolicyException>(() => new MutationService(policy).CreateFileAsync("parent/new.txt", "changed"));
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "old-parent/new.txt")));
    }
}
