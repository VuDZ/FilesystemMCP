using System.Text;
using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Spec", "FS-11")]
public sealed class EmptyReplacementTests
{
    /// <summary>The tool's snippet window; the long-file assertions pin this contract.</summary>
    private const int MaxSnippetLength = 400;

    // ---- deletion and whitespace replacement are valid ----------------------
    // These four cases were the KnownDefect matrix before 1.11.0; FS-11 turns them
    // into the contract: "", " ", "\n", "\t" and any combination are replacements,
    // and a whitespace-only target matches exactly, without trimming.

    [Theory, Trait("Status", "Baseline")]
    [InlineData("abc", "b", "", "ac")]
    [InlineData("abc", "abc", "", "")]
    [InlineData("abc", "b", " \n\t", "a \n\tc")]
    [InlineData("a  b", "  ", "_", "a_b")]
    public async Task EmptyAndWhitespaceSnippetsAreValid(string original, string target, string replacement, string expected)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", original);
        var payload = await ReplaceAsync(sandbox, target, replacement);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes(expected).Sha256, payload.GetProperty("new_hash").GetString());
        // The result is shorter than the window, so the snippet is the whole normalized result.
        Assert.Equal(expected, payload.GetProperty("snippet").GetString());
    }

    // ---- argument validation ------------------------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task EmptyTargetIsRejectedWithoutWrite()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        var service = new FileService(sandbox.Workspace);
        var rawArgs = ServerProcess.Arguments(new { path = "file.txt", target_snippet = "", replacement_snippet = "new", original_hash = FileTextHelper.ComputeContentHashes("original").Sha256 });
        await Assert.ThrowsAsync<ArgumentException>(() => new ReplaceInFileTool(service).ExecuteAsync(rawArgs, default));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task MissingReplacementIsRejectedWithoutWrite()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        var service = new FileService(sandbox.Workspace);
        var args = ServerProcess.Arguments(new
        {
            path = "file.txt", target_snippet = "original",
            original_hash = FileTextHelper.ComputeContentHashes("original").Sha256
        });
        await Assert.ThrowsAsync<ArgumentException>(() => new ReplaceInFileTool(service).ExecuteAsync(args, default));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    /// <summary>
    /// Missing, JSON null, wrong type (number, object, array) and a whitespace-only hash are
    /// all argument failures at the tool boundary: no implicit empty default, no trim of the
    /// hash, and no write. Each fragment isolates one argument; the others stay valid.
    /// </summary>
    [Theory, Trait("Status", "Baseline")]
    [InlineData("""{ "path": "file.txt", "target_snippet": 42, "replacement_snippet": "x", "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": null, "replacement_snippet": "x", "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": { "a": 1 }, "replacement_snippet": "x", "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": ["original"], "replacement_snippet": "x", "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "replacement_snippet": "x", "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": 42, "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": null, "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": { "a": 1 }, "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": ["x"], "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "original_hash": "00" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": "x", "original_hash": 42 }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": "x", "original_hash": null }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": "x", "original_hash": { "a": 1 } }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": "x", "original_hash": ["00"] }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": "x" }""")]
    [InlineData("""{ "path": "file.txt", "target_snippet": "original", "replacement_snippet": "x", "original_hash": "   " }""")]
    public async Task WrongTypesNullsAndBlankHashAreRejectedWithoutWrite(string fragment)
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        var service = new FileService(sandbox.Workspace);
        var args = ServerProcess.JsonDocumentParse(fragment);
        await Assert.ThrowsAsync<ArgumentException>(() => new ReplaceInFileTool(service).ExecuteAsync(args, default));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    /// <summary>
    /// The same failures over the wire are JSON-RPC invalid-params (-32602), not a tool
    /// result with isError; no file and no temp is written. Every wrong shape the direct
    /// theory covers for the three text arguments is exercised here too: number, object,
    /// array, JSON null and a missing property.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task WrongTypesNullsAndBlankHashAreInvalidParams()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        var hash = FileTextHelper.ComputeContentHashes("original").Sha256;
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        // target_snippet: number, object, array, null, missing.
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = 42, replacement_snippet = "x", original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = new { a = 1 }, replacement_snippet = "x", original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = new[] { "original" }, replacement_snippet = "x", original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = (string?)null, replacement_snippet = "x", original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", replacement_snippet = "x", original_hash = hash }), -32602);
        // replacement_snippet: number, object, array, null, missing.
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = 42, original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = new { a = 1 }, original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = new[] { "x" }, original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = (string?)null, original_hash = hash }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", original_hash = hash }), -32602);
        // original_hash: number, object, array, null, missing, whitespace-only, empty target.
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = "x", original_hash = 42 }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = "x", original_hash = new { a = 1 } }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = "x", original_hash = new[] { hash } }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = "x", original_hash = (string?)null }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = "x" }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = "x", original_hash = "   " }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "", replacement_snippet = "x", original_hash = hash }), -32602);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    /// <summary>
    /// The published inputSchema and the runtime validator must describe the same three text
    /// arguments. Nothing else asserts the schema (it is only published by tools/list and the
    /// server never validates against it), so without this case a pattern could be added to or
    /// removed from the contract without a single failing test — which is exactly how the
    /// whitespace-only target rule was first broken.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task PublishedSchemaMatchesTheRuntimeRulesForTheTextArguments()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        var hash = FileTextHelper.ComputeContentHashes("original").Sha256;
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var tools = (await server.CallAsync("tools/list", new { })).GetProperty("result").GetProperty("tools");
        var tool = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "replace_in_file");
        var schema = tool.GetProperty("inputSchema");
        var properties = schema.GetProperty("properties");
        var required = schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray();

        // replacement_snippet: any string, including "" — no length or pattern restriction.
        var replacement = RequiredProperty(properties, "replacement_snippet");
        Assert.Equal("string", replacement.GetProperty("type").GetString());
        Assert.False(replacement.TryGetProperty("minLength", out _));
        Assert.False(replacement.TryGetProperty("pattern", out _));

        // target_snippet: non-empty by Length only, so a whitespace-only target stays legal.
        var target = RequiredProperty(properties, "target_snippet");
        Assert.Equal("string", target.GetProperty("type").GetString());
        Assert.Equal(1, target.GetProperty("minLength").GetInt32());
        Assert.False(target.TryGetProperty("pattern", out _));

        // original_hash: no whitespace-only value is a hash, so the schema says so explicitly.
        var originalHash = RequiredProperty(properties, "original_hash");
        Assert.Equal("string", originalHash.GetProperty("type").GetString());
        Assert.Equal(1, originalHash.GetProperty("minLength").GetInt32());
        var pattern = originalHash.GetProperty("pattern").GetString()!;
        Assert.Equal("\\S", pattern);
        Assert.DoesNotMatch(pattern, "   ");
        Assert.Matches(pattern, hash);

        foreach (var name in new[] { "target_snippet", "replacement_snippet", "original_hash" })
        {
            Assert.Contains(name, required);
        }

        // The behaviour the schema describes, over the same wire. A whitespace-only target is
        // accepted as an argument and fails as a search that found nothing — not as -32602.
        var whitespaceTarget = await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "  ", replacement_snippet = "_", original_hash = hash });
        Assert.False(whitespaceTarget.TryGetProperty("error", out _), "The schema allows this target: " + whitespaceTarget);
        McpAssert.ToolError(whitespaceTarget, "target_not_found");
        // A whitespace-only hash is not a hash, and an empty target is empty by Length.
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "original", replacement_snippet = "_", original_hash = "   " }), -32602);
        McpAssert.ProtocolError(await server.ToolAsync("replace_in_file",
            new { path = "file.txt", target_snippet = "", replacement_snippet = "_", original_hash = hash }), -32602);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    private static JsonElement RequiredProperty(JsonElement properties, string name)
    {
        Assert.True(properties.TryGetProperty(name, out var property), "The schema must declare " + name);
        return property;
    }

    /// <summary>
    /// Surrounding whitespace in a hash is not trimmed into a match: it is a mismatch, on
    /// either side. A whitespace-only value is not a hash at all and fails as an argument.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task HashWhitespaceIsNotTrimmed()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "original");
        var service = new FileService(sandbox.Workspace);
        var read = await service.ReadFileAsync("file.txt", new());
        foreach (var padded in new[] { " " + read.Sha256, read.Sha256 + " ", "\t" + read.Sha256, read.Sha256 + "\r\n" })
        {
            var args = ServerProcess.Arguments(new
            {
                path = "file.txt", target_snippet = "original", replacement_snippet = "new",
                original_hash = padded
            });
            var error = await Assert.ThrowsAsync<MutationException>(() => new ReplaceInFileTool(service).ExecuteAsync(args, default));
            Assert.Equal("hash_conflict", error.Code);
        }

        var blank = ServerProcess.Arguments(new
        {
            path = "file.txt", target_snippet = "original", replacement_snippet = "new", original_hash = "   "
        });
        await Assert.ThrowsAsync<ArgumentException>(() => new ReplaceInFileTool(service).ExecuteAsync(blank, default));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Workspace, ".filesystemmcp-*.tmp"));
    }

    // ---- newline replacement keeps the stored format (FS-03) ----------------

    [Fact, Trait("Status", "Baseline")]
    public async Task NewlineReplacementInCrlfFileKeepsStoredLineStyle()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "a\r\nb\r\n");
        var payload = await ReplaceAsync(sandbox, "b", "\n");
        // The surrounding document is CRLF, so the inserted newline is stored as CRLF too.
        const string stored = "a\r\n\r\n\r\n";
        Assert.Equal(Sandbox.Utf8.GetBytes(stored), await File.ReadAllBytesAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes("a\n\n\n").Sha256, payload.GetProperty("new_hash").GetString());
        Assert.Equal("a\n\n\n", payload.GetProperty("snippet").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task NewlineReplacementInUtf16FileKeepsEncodingAndBom()
    {
        using var sandbox = new Sandbox();
        var encoding = TextEncodingTests.EncodingFor("utf16le");
        var path = sandbox.Write("file.txt", "one two", encoding);
        var payload = await ReplaceAsync(sandbox, " ", "\n");
        const string stored = "one\ntwo";
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(stored)).ToArray(), await File.ReadAllBytesAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes(stored).Sha256, payload.GetProperty("new_hash").GetString());
        Assert.Equal(stored, payload.GetProperty("snippet").GetString());
    }

    /// <summary>
    /// Deleting the whole content leaves a valid empty file in the original encoding: zero
    /// bytes for UTF-8 without BOM and exactly the original BOM for the five BOM encodings.
    /// </summary>
    [Theory, Trait("Status", "Baseline")]
    [InlineData("utf8")] [InlineData("utf8bom")]
    [InlineData("utf16le")] [InlineData("utf16be")]
    [InlineData("utf32le")] [InlineData("utf32be")]
    public async Task DeletingAllContentKeepsTheEmptyFileInItsOriginalEncoding(string encodingName)
    {
        using var sandbox = new Sandbox();
        var encoding = TextEncodingTests.EncodingFor(encodingName);
        var path = sandbox.Write("file.txt", "one two", encoding);
        var payload = await ReplaceAsync(sandbox, "one two", "");
        Assert.Equal(encoding.GetPreamble(), await File.ReadAllBytesAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes("").Sha256, payload.GetProperty("new_hash").GetString());
        Assert.Equal("", payload.GetProperty("snippet").GetString());
        var reread = await new FileService(sandbox.Workspace).ReadFileAsync("file.txt", new());
        Assert.Equal("", reread.Text);
        Assert.Equal(FileTextHelper.ComputeContentHashes("").Sha256, reread.Sha256);
    }

    // ---- delimiter and first-match semantics --------------------------------

    [Fact, Trait("Status", "Baseline")]
    public async Task NewlineTargetResolvesToTheFirstDelimiter()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "a\nb\n");
        var payload = await ReplaceAsync(sandbox, "\n", "");
        // First match, not the terminal delimiter: the file keeps its final newline.
        Assert.Equal("ab\n", await File.ReadAllTextAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes("ab\n").Sha256, payload.GetProperty("new_hash").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task TerminalDelimiterCanBeDeleted()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "a\nb\n");
        var payload = await ReplaceAsync(sandbox, "b\n", "");
        Assert.Equal("a\n", await File.ReadAllTextAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes("a\n").Sha256, payload.GetProperty("new_hash").GetString());
        Assert.Equal("a\n", payload.GetProperty("snippet").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SeveralIdenticalTargetsEditOnlyTheFirst()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "TOKEN one\nTOKEN two\nTOKEN three");
        var payload = await ReplaceAsync(sandbox, "TOKEN", "X");
        const string expected = "X one\nTOKEN two\nTOKEN three";
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes(expected).Sha256, payload.GetProperty("new_hash").GetString());
        Assert.Equal(expected, payload.GetProperty("snippet").GetString());
        // Deletion is first-match too: the second and third occurrences survive.
        sandbox.Write("file.txt", "TOKEN one\nTOKEN two\nTOKEN three");
        var deleted = await ReplaceAsync(sandbox, "TOKEN ", "");
        const string afterDeletion = "one\nTOKEN two\nTOKEN three";
        Assert.Equal(afterDeletion, await File.ReadAllTextAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes(afterDeletion).Sha256, deleted.GetProperty("new_hash").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task DeletingEverythingLeavesAnEmptyFileWithAValidHash()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", "all content");
        var payload = await ReplaceAsync(sandbox, "all content", "");
        Assert.Empty(await File.ReadAllBytesAsync(path));
        Assert.Equal("", payload.GetProperty("snippet").GetString());
        Assert.Equal(FileTextHelper.ComputeContentHashes("").Sha256, payload.GetProperty("new_hash").GetString());
        var reread = await new FileService(sandbox.Workspace).ReadFileAsync("file.txt", new());
        Assert.Equal("", reread.Text);
        Assert.Equal(FileTextHelper.ComputeContentHashes("").Sha256, reread.Sha256);
    }

    // ---- the snippet is anchored on the edit, not on the replacement text ---

    /// <summary>
    /// The replacement text already occurs before the edit. Pre-1.11.0 the snippet was built
    /// around the first occurrence of that text (offset 100 window); the contract is the window
    /// around the offset the edit actually happened at.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task SnippetIsAnchoredAtTheEditNotAtAnEarlierOccurrenceOfTheReplacement()
    {
        using var sandbox = new Sandbox();
        const string token = "EARLYTOKEN";
        var original = new string('a', 300) + token + new string('b', 300) + "OLD" + new string('c', 300);
        var path = sandbox.Write("file.txt", original);
        var payload = await ReplaceAsync(sandbox, "OLD", token);
        var result = new string('a', 300) + token + new string('b', 300) + token + new string('c', 300);
        var anchor = 300 + token.Length + 300; // 610: the second token is the edit
        var start = anchor - (MaxSnippetLength / 2);
        var snippet = payload.GetProperty("snippet").GetString()!;
        Assert.Equal(result.Substring(start, MaxSnippetLength), snippet);
        Assert.Equal(1, Occurrences(snippet, token));
        Assert.NotEqual(result.Substring(300 - (MaxSnippetLength / 2), MaxSnippetLength), snippet);
        Assert.Equal(result, await File.ReadAllTextAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes(result).Sha256, payload.GetProperty("new_hash").GetString());
    }

    /// <summary>
    /// A deletion has no replacement text to search for; "" matched at index 0 before 1.11.0,
    /// so the window described the head of the file instead of the deleted range.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task SnippetForDeletionIsAnchoredAtTheDeletedRange()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", new string('x', 300) + "DELETEME" + new string('y', 300));
        var payload = await ReplaceAsync(sandbox, "DELETEME", "");
        var result = new string('x', 300) + new string('y', 300);
        var snippet = payload.GetProperty("snippet").GetString()!;
        Assert.Equal(result.Substring(100, MaxSnippetLength), snippet);
        Assert.NotEqual(result[..MaxSnippetLength], snippet);
        Assert.Equal(result, await File.ReadAllTextAsync(path));
        Assert.Equal(FileTextHelper.ComputeContentHashes(result).Sha256, payload.GetProperty("new_hash").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SnippetAtTheStartOfALongResultIsTheLeadingWindow()
    {
        using var sandbox = new Sandbox();
        // The edit sits at offset 0, so the window is clamped to the head of the result.
        // Both algorithms agree on this boundary; it pins the clamp, not the anchor.
        var path = sandbox.Write("file.txt", "HEAD" + new string('z', 500));
        var payload = await ReplaceAsync(sandbox, "HEAD", "H");
        var result = "H" + new string('z', 500);
        Assert.Equal(result[..MaxSnippetLength], payload.GetProperty("snippet").GetString());
        Assert.Equal(result, await File.ReadAllTextAsync(path));

        // The edit is 209 characters in and the replacement text also stands at offset 0.
        // Pre-1.11.0 the window started at that earlier occurrence (0); the anchor puts it at
        // 9 = 209 - 200, which still holds the inserted text and leaves the old window behind.
        sandbox.Write("file.txt", "MARK" + new string('z', 205) + "OLD" + new string('z', 300));
        var anchored = await ReplaceAsync(sandbox, "OLD", "MARK");
        var anchoredResult = "MARK" + new string('z', 205) + "MARK" + new string('z', 300);
        var snippet = anchored.GetProperty("snippet").GetString()!;
        Assert.Equal(anchoredResult.Substring(9, MaxSnippetLength), snippet);
        Assert.NotEqual(anchoredResult[..MaxSnippetLength], snippet);
        Assert.Equal(1, Occurrences(snippet, "MARK"));
        Assert.Equal(anchoredResult, await File.ReadAllTextAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SnippetAtTheEndOfALongResultKeepsTheTail()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", new string('z', 500) + "TAIL");
        var payload = await ReplaceAsync(sandbox, "TAIL", "");
        var result = new string('z', 500);
        // The anchor sits at the end of the result, so the window has only its 200-char tail.
        // Discriminating: the old IndexOf("")-at-0 window was the 400-character head.
        Assert.Equal(new string('z', 200), payload.GetProperty("snippet").GetString());
        Assert.Equal(result, await File.ReadAllTextAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SnippetAtExactlyMaxSnippetLengthIsTheWholeResult()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write("file.txt", new string('z', MaxSnippetLength) + "AB");
        var payload = await ReplaceAsync(sandbox, "AB", "");
        var result = new string('z', MaxSnippetLength);
        // The result is exactly at the boundary: the "text.Length <= MaxSnippetLength" branch
        // returns the whole text in the old implementation too, so this case pins the
        // preserved behaviour of the spec rather than discriminating the anchor. The
        // discriminating coverage of the clipping boundary is the 401-character case below.
        var snippet = payload.GetProperty("snippet").GetString()!;
        Assert.Equal(MaxSnippetLength, snippet.Length);
        Assert.Equal(result, snippet);
        Assert.Equal(result, await File.ReadAllTextAsync(path));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SnippetOneCharacterOverMaxSnippetLengthIsClippedToTheWindow()
    {
        using var sandbox = new Sandbox();
        // Edit at the end of a 401-character result: the window keeps the 200-character tail.
        // Pre-1.11.0 the empty replacement matched at offset 0 and the window was the head.
        var path = sandbox.Write("file.txt", new string('z', 401) + "AB");
        var payload = await ReplaceAsync(sandbox, "AB", "");
        var result = new string('z', 401);
        Assert.Equal(new string('z', 200), payload.GetProperty("snippet").GetString());
        Assert.Equal(result, await File.ReadAllTextAsync(path));

        // Edit at offset 201 of a 401-character result: the window starts one character in,
        // which the offset-0 window of the old IndexOf("") algorithm could not produce.
        sandbox.Write("file.txt", "P" + new string('x', 200) + "AB" + new string('y', 200));
        var leading = await ReplaceAsync(sandbox, "AB", "");
        var leadingResult = "P" + new string('x', 200) + new string('y', 200);
        Assert.Equal(401, leadingResult.Length);
        var leadingSnippet = leading.GetProperty("snippet").GetString()!;
        Assert.Equal(leadingResult.Substring(1, MaxSnippetLength), leadingSnippet);
        Assert.NotEqual(leadingResult[..MaxSnippetLength], leadingSnippet);
        Assert.Equal(leadingResult, await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// The anchor is a canonical (LF-normalized) offset, not a raw-file offset. Here they
    /// differ by a factor of 1.5: the canonical edit starts at 300, the raw one at 450, so a
    /// raw-offset window would start at 250 instead of 100.
    /// </summary>
    [Fact, Trait("Status", "Baseline")]
    public async Task SnippetInCrlfFileUsesTheCanonicalOffsetOfTheEdit()
    {
        using var sandbox = new Sandbox();
        var original = string.Concat(Enumerable.Repeat("A\r\n", 150)) + "OLD\r\n" + string.Concat(Enumerable.Repeat("B\r\n", 150));
        var path = sandbox.Write("file.txt", original);
        var payload = await ReplaceAsync(sandbox, "OLD", "NEW");
        var result = string.Concat(Enumerable.Repeat("A\n", 150)) + "NEW\n" + string.Concat(Enumerable.Repeat("B\n", 150));
        Assert.Equal(604, result.Length);
        var anchor = 150 * 2; // canonical offset of the edit; the raw offset is 150 * 3
        var start = anchor - (MaxSnippetLength / 2);
        var snippet = payload.GetProperty("snippet").GetString()!;
        Assert.Equal(result.Substring(start, MaxSnippetLength), snippet);
        // The window a raw-offset implementation would produce, for contrast.
        var rawStart = (150 * 3) - (MaxSnippetLength / 2);
        var rawWindow = result.Substring(rawStart, Math.Min(MaxSnippetLength, result.Length - rawStart));
        Assert.NotEqual(rawWindow, snippet);
        Assert.Contains("NEW", snippet);
        Assert.Equal(FileTextHelper.ComputeContentHashes(result).Sha256, payload.GetProperty("new_hash").GetString());
        var stored = string.Concat(Enumerable.Repeat("A\r\n", 150)) + "NEW\r\n" + string.Concat(Enumerable.Repeat("B\r\n", 150));
        Assert.Equal(Sandbox.Utf8.GetBytes(stored), await File.ReadAllBytesAsync(path));
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<JsonElement> ReplaceAsync(Sandbox sandbox, string target, string replacement)
    {
        var service = new FileService(sandbox.Workspace);
        var read = await service.ReadFileAsync("file.txt", new());
        var raw = await new ReplaceInFileTool(service).ExecuteAsync(ServerProcess.Arguments(new
        {
            path = "file.txt", target_snippet = target, replacement_snippet = replacement, original_hash = read.Sha256
        }), default);
        return ServerProcess.JsonDocumentParse(raw);
    }

    private static int Occurrences(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
