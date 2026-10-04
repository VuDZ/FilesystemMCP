using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-12")]
public sealed class UnifiedApiTests
{
    [Theory, Trait("Status", "Baseline")]
    [InlineData("read_file"), InlineData("create_file"), InlineData("replace_in_file"), InlineData("list_directory"), InlineData("search"), InlineData("append_to_file")]
    public async Task CustomRpcMethodsAreRejectedInsteadOfExecutingDivergentOrStubOperations(string method)
    {
        using var sandbox = new Sandbox();
        var original = sandbox.Write("file.txt", "old");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.CallAsync(method, new
        {
            path = method == "list_directory" ? "." : method == "create_file" ? "new.txt" : "file.txt",
            content = "new", regex = "old", file_mask = "*.txt", target_snippet = "old", replacement_snippet = "changed",
            original_hash = FileTextHelper.ComputeContentHashes("old").Sha256
        });
        Assert.True(reply.TryGetProperty("error", out var error), "Custom method must be unsupported: " + reply);
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
        Assert.Equal("old", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "new.txt")));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ConflictingPathAliasesAreRejected()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("one.txt", "one");
        sandbox.Write("two.txt", "two");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var reply = await server.ToolAsync("read_file", new { path = "one.txt", file_path = "two.txt" });
        McpAssert.ProtocolError(reply, -32602);
    }

    [Fact, Trait("Status", "Baseline")]
    public void AgentSampleDoesNotAdvertiseAbsentAppendTool()
    {
        var sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "AGENTS.md.sample"));
        Assert.DoesNotContain("append_to_file", sample);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task StandardCreateDoesNotOverwriteExistingFile()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("existing.txt", "valuable");
        var error = await Assert.ThrowsAsync<MutationException>(() => new CreateFileTool(sandbox.Workspace).ExecuteAsync(
            ServerProcess.Arguments(new { path = "existing.txt", content = "replacement" }), default));
        Assert.Equal("file_exists", error.Code);
        Assert.Equal("valuable", await File.ReadAllTextAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SchemaAndRuntimeAgreeOnAliasesTypesAndUnknownProperties()
    {
        using var sandbox = new Sandbox();
        var original = sandbox.Write("file.txt", "old");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var tools = (await server.CallAsync("tools/list", new { })).GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "create_file", "list_directory", "read_file", "replace_in_file", "search" }, names);

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            var schema = tool.GetProperty("inputSchema");
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean(), name);
            if (name == "search")
            {
                Assert.False(schema.TryGetProperty("anyOf", out _), name);
                continue;
            }

            var branches = schema.GetProperty("anyOf").EnumerateArray()
                .Select(branch => branch.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Single())
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "filePath", "file_path", "path" }, branches);
            foreach (var alias in new[] { "path", "filePath", "file_path" })
            {
                var property = schema.GetProperty("properties").GetProperty(alias);
                Assert.Equal("string", property.GetProperty("type").GetString());
                Assert.Equal(1, property.GetProperty("minLength").GetInt32());
                // JSON Schema pattern is a substring search: \S means "contains a non-whitespace
                // character", the same set PathPolicy rejects with IsNullOrWhiteSpace for " ".
                var pattern = property.GetProperty("pattern").GetString()!;
                Assert.Equal("\\S", pattern);
                Assert.DoesNotMatch(pattern, " ");
                Assert.Matches(pattern, "file.txt");
            }
        }

        var hash = FileTextHelper.ComputeContentHashes("old").Sha256;
        foreach (var alias in new[] { "path", "filePath", "file_path" })
        {
            McpAssert.Success(await server.ToolAsync("read_file", new Dictionary<string, object> { [alias] = "file.txt" }));
            McpAssert.Success(await server.ToolAsync("list_directory", new Dictionary<string, object> { [alias] = "." }));
            McpAssert.Success(await server.ToolAsync("create_file", new Dictionary<string, object>
            {
                [alias] = "made-" + alias + ".txt",
                ["content"] = "x"
            }));
            McpAssert.Success(await server.ToolAsync("replace_in_file", new Dictionary<string, object>
            {
                [alias] = "file.txt",
                ["target_snippet"] = "old",
                ["replacement_snippet"] = "old",
                ["original_hash"] = hash
            }));
        }

        McpAssert.Success(await server.ToolAsync("read_file", new { path = "file.txt", filePath = "file.txt", file_path = "file.txt" }));
        McpAssert.ProtocolError(await server.ToolAsync("read_file", new { path = "file.txt", file_path = "other.txt" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("list_directory", new { path = ".", filePath = "nope" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("create_file", new { path = "left.txt", filePath = "right.txt", content = "z" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file", new
        {
            path = "file.txt", file_path = "other.txt", target_snippet = "old", replacement_snippet = "new", original_hash = hash
        }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("read_file", new { path = "file.txt", extra = 1 }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("list_directory", new { path = ".", extra = true }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("create_file", new { path = "z.txt", content = "z", extra = "no" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file", new
        {
            path = "file.txt", target_snippet = "old", replacement_snippet = "new", original_hash = hash, extra = 1
        }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("search", new { regex = "old", extra = 1 }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("search", new { regex = "old", path = "file.txt" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("read_file", new { path = 1 }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("list_directory", new { filePath = 2 }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("create_file", new { file_path = true, content = "z" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file", new { path = new { nested = "file.txt" }, target_snippet = "old", replacement_snippet = "new", original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("read_file", new { path = "" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("read_file", new { filePath = (string?)null }), -32602);
        foreach (var alias in new[] { "path", "filePath", "file_path" })
        {
            McpAssert.ProtocolError(await server.ToolAsync("read_file", new Dictionary<string, object> { [alias] = " " }), -32602);
            McpAssert.ProtocolError(await server.ToolAsync("list_directory", new Dictionary<string, object> { [alias] = " " }), -32602);
            McpAssert.ProtocolError(await server.ToolAsync("create_file", new Dictionary<string, object>
            {
                [alias] = " ",
                ["content"] = "z"
            }), -32602);
            McpAssert.ProtocolError(await server.ToolAsync("replace_in_file", new Dictionary<string, object>
            {
                [alias] = " ",
                ["target_snippet"] = "old",
                ["replacement_snippet"] = "new",
                ["original_hash"] = hash
            }), -32602);
        }
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, " ")));

        Assert.Equal("old", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "left.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "right.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "z.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox.Workspace, "other.txt")));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task CreateReplaceAndReadSharePathAndHashes()
    {
        using var sandbox = new Sandbox();
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var created = ServerProcess.Payload(await server.ToolAsync("create_file", new { filePath = "note.txt", content = "one\r\ntwo\r\n" }));
        Assert.Equal("success", created.GetProperty("status").GetString());
        Assert.Equal("note.txt", Path.GetFileName(created.GetProperty("path").GetString()));
        var sha = created.GetProperty("sha256").GetString();
        var md5 = created.GetProperty("md5").GetString();
        Assert.Equal(FileTextHelper.ComputeContentHashes("one\ntwo\n").Sha256, sha);
        Assert.Equal(FileTextHelper.ComputeContentHashes("one\ntwo\n").Md5, md5);
        Assert.Equal("one\r\ntwo\r\n", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "note.txt")));

        var read = ServerProcess.Payload(await server.ToolAsync("read_file", new { file_path = "note.txt" }));
        Assert.Equal(sha, read.GetProperty("sha256").GetString());
        Assert.Equal(md5, read.GetProperty("md5").GetString());
        Assert.Equal(created.GetProperty("path").GetString(), read.GetProperty("path").GetString());

        var replaced = ServerProcess.Payload(await server.ToolAsync("replace_in_file", new
        {
            path = "note.txt",
            filePath = "note.txt",
            file_path = "note.txt",
            target_snippet = "two",
            replacement_snippet = "three",
            original_hash = sha
        }));
        Assert.Equal("success", replaced.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(replaced.GetProperty("snippet").GetString()));
        Assert.Equal(replaced.GetProperty("sha256").GetString(), replaced.GetProperty("new_hash").GetString());
        Assert.Equal(created.GetProperty("path").GetString(), replaced.GetProperty("path").GetString());
        Assert.Equal(FileTextHelper.ComputeContentHashes("one\nthree\n").Sha256, replaced.GetProperty("sha256").GetString());
        Assert.Equal(FileTextHelper.ComputeContentHashes("one\nthree\n").Md5, replaced.GetProperty("md5").GetString());
        Assert.Equal("one\r\nthree\r\n", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "note.txt")));

        var reread = ServerProcess.Payload(await server.ToolAsync("read_file", new { path = "note.txt" }));
        Assert.Equal(replaced.GetProperty("sha256").GetString(), reread.GetProperty("sha256").GetString());
        Assert.Equal(replaced.GetProperty("md5").GetString(), reread.GetProperty("md5").GetString());
        Assert.Equal(replaced.GetProperty("path").GetString(), reread.GetProperty("path").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task CreateReadAndReplaceShareOneServiceAndTheDefaultBudget()
    {
        using var sandbox = new Sandbox();
        var operations = new FileOperationsService(sandbox.Workspace);
        Assert.Equal(ResourceBudget.Default, operations.Budget);
        var create = new CreateFileTool(operations);
        var read = new ReadFileTool(operations);
        var replace = new ReplaceInFileTool(operations);

        var created = ServerProcess.JsonDocumentParse(await create.ExecuteAsync(
            ServerProcess.Arguments(new { path = "shared.txt", content = "one\r\ntwo\r\n" }), default));
        var standalone = ServerProcess.JsonDocumentParse(await new CreateFileTool(sandbox.Workspace).ExecuteAsync(
            ServerProcess.Arguments(new { file_path = "other.txt", content = "one\r\ntwo\r\n" }), default));
        Assert.Equal(created.GetProperty("sha256").GetString(), standalone.GetProperty("sha256").GetString());
        Assert.Equal(created.GetProperty("md5").GetString(), standalone.GetProperty("md5").GetString());
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(sandbox.Workspace, "shared.txt")),
            await File.ReadAllBytesAsync(Path.Combine(sandbox.Workspace, "other.txt")));

        var readBack = ServerProcess.JsonDocumentParse(await read.ExecuteAsync(
            ServerProcess.Arguments(new { filePath = "shared.txt" }), default));
        Assert.Equal(created.GetProperty("sha256").GetString(), readBack.GetProperty("sha256").GetString());
        Assert.Equal(created.GetProperty("md5").GetString(), readBack.GetProperty("md5").GetString());
        Assert.Equal(created.GetProperty("path").GetString(), readBack.GetProperty("path").GetString());

        var replaced = ServerProcess.JsonDocumentParse(await replace.ExecuteAsync(ServerProcess.Arguments(new
        {
            file_path = "shared.txt",
            target_snippet = "two",
            replacement_snippet = "three",
            original_hash = created.GetProperty("sha256").GetString()
        }), default));
        Assert.Equal(replaced.GetProperty("sha256").GetString(), replaced.GetProperty("new_hash").GetString());
        Assert.Equal(created.GetProperty("path").GetString(), replaced.GetProperty("path").GetString());
        var serviceRead = await operations.ReadFileAsync("shared.txt", new());
        Assert.Equal(replaced.GetProperty("sha256").GetString(), serviceRead.Sha256);
        Assert.Equal(replaced.GetProperty("md5").GetString(), serviceRead.Md5);
        Assert.Equal(replaced.GetProperty("path").GetString(), serviceRead.Path);
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ConcurrentCreateDoesNotReplaceTheWinnerBytes()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sandbox.Workspace, "real"));
        await using var firstGate = new ProcessGate();
        await using var secondGate = new ProcessGate();
        await using var first = await firstGate.Start(sandbox.Workspace);
        await using var second = await secondGate.Start(sandbox.Workspace);
        var firstWrite = await first.SendToolAsync("create_file", new { path = "real/file.txt", content = "first" });
        await firstGate.At("BeforeLock");
        await firstGate.Release();
        await firstGate.At("BeforeCommit");
        var secondWrite = await second.SendToolAsync("create_file", new { path = "real/file.txt", content = "second" });
        await secondGate.At("BeforeLock");
        await secondGate.Release();
        await secondGate.At("LockContended");
        await secondGate.Release();
        await firstGate.Release();
        var success = await first.ReadResponseAsync(firstWrite);
        var conflict = await second.ReadResponseAsync(secondWrite);
        Assert.False(success.GetProperty("result").GetProperty("isError").GetBoolean());
        McpAssert.ToolError(conflict, "file_exists");
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, "real/file.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(sandbox.Workspace, "real"), ".filesystemmcp-*.tmp"));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ResultsSerializeThroughTheSourceGeneratedContext()
    {
        var created = new CreateFileToolResult("success", "note.txt", "md5", "sha");
        Assert.Equal(
            "{\"status\":\"success\",\"path\":\"note.txt\",\"md5\":\"md5\",\"sha256\":\"sha\"}",
            JsonSerializer.Serialize(created, McpJsonContext.Default.CreateFileToolResult));

        var replaced = new ReplaceInFileToolResult("success", "note.txt", "md5", "sha", "sha", "one\nthree\n");
        Assert.Equal(
            "{\"status\":\"success\",\"path\":\"note.txt\",\"md5\":\"md5\",\"sha256\":\"sha\",\"new_hash\":\"sha\",\"snippet\":\"one\\nthree\\n\"}",
            JsonSerializer.Serialize(replaced, McpJsonContext.Default.ReplaceInFileToolResult));

        var listed = new ListDirectoryResult([new DirectoryEntry("note.txt", "file")]);
        Assert.Equal(
            "{\"entries\":[{\"name\":\"note.txt\",\"type\":\"file\"}]}",
            JsonSerializer.Serialize(listed, McpJsonContext.Default.ListDirectoryResult));
        var truncatedList = new ListDirectoryResult([], true, ListDirectoryTool.TruncationReasonMaxResponseChars);
        Assert.Equal(
            "{\"entries\":[],\"truncated\":true,\"truncation_reason\":\"max_response_chars\"}",
            JsonSerializer.Serialize(truncatedList, McpJsonContext.Default.ListDirectoryResult));

        var search = new SearchResult(
            [new SearchMatch("note.txt", 2)],
            false,
            true,
            1,
            [new SearchSkip("bin", "access_denied", "access_denied")]);
        Assert.Equal(
            "{\"matches\":[{\"path\":\"note.txt\",\"line\":2}],\"truncated\":false,\"incomplete\":true,"
            + "\"skipped_count\":1,\"skipped\":[{\"path\":\"bin\",\"reason\":\"access_denied\",\"code\":\"access_denied\"}]}",
            JsonSerializer.Serialize(search, McpJsonContext.Default.SearchResult));

        using var sandbox = new Sandbox();
        sandbox.Write("note.txt", "alpha\nbeta\n");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var liveList = ServerProcess.Payload(await server.ToolAsync("list_directory", new { path = "." }));
        var entry = Assert.Single(liveList.GetProperty("entries").EnumerateArray());
        Assert.Equal("note.txt", entry.GetProperty("name").GetString());
        Assert.Equal("file", entry.GetProperty("type").GetString());
        Assert.False(liveList.TryGetProperty("truncated", out _));

        var liveSearch = ServerProcess.Payload(await server.ToolAsync("search", new { regex = "beta" }));
        var match = Assert.Single(liveSearch.GetProperty("matches").EnumerateArray());
        Assert.Equal("note.txt", match.GetProperty("path").GetString());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        Assert.False(liveSearch.GetProperty("truncated").GetBoolean());
        Assert.False(liveSearch.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0, liveSearch.GetProperty("skipped_count").GetInt32());
        Assert.Empty(liveSearch.GetProperty("skipped").EnumerateArray());
    }
}
