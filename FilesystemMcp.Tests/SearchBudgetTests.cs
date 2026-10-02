using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

/// <summary>
/// FS-07 acceptance for the search and list_directory budgets: the traversal caps, the
/// response-character budget, the operation deadline and the split between that deadline
/// and a peer cancellation. Every case is deterministic — caps are observed through the
/// tools' internal seams and no test synchronizes on a sleep or on wall-clock time.
/// </summary>
/// <remarks>
/// The class is public because xUnit.net v2 only discovers public test classes; every
/// other test class in this project is public for the same reason. The tools' internal
/// seams are reachable through <c>InternalsVisibleTo</c>.
/// </remarks>
[Trait("Spec", "FS-07")]
public sealed class SearchBudgetTests
{
    // ---- Traversal budgets over stdio: small CLI values return a partial result, not an error ----

    [Fact, Trait("Status", "Baseline")]
    public async Task SearchMaxFilesStopsTraversalAndReturnsPartialResult()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("a.txt", "needle a");
        sandbox.Write("b.txt", "needle b");
        sandbox.Write("c.txt", "needle c");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--searchMaxFiles=1"]);

        var reply = await server.ToolAsync("search", new { regex = "needle", file_mask = "*.txt" });

        // A budget is a bounded partial result, never a protocol error and never a
        // truncated JSON text: Payload() would throw on invalid JSON.
        McpAssert.Success(reply);
        var payload = ServerProcess.Payload(reply);
        Assert.Equal(SearchTool.TruncationReasonSearchMaxFiles, payload.GetProperty("truncation_reason").GetString());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.Equal(1, payload.GetProperty("matches").GetArrayLength());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SearchMaxDirectoriesStopsTraversalAndReturnsPartialResult()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("root.txt", "needle root");
        sandbox.Write("dirA/a.txt", "needle a");
        sandbox.Write("dirB/b.txt", "needle b");
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--searchMaxDirectories=1"]);

        var reply = await server.ToolAsync("search", new { regex = "needle", file_mask = "*.txt" });

        McpAssert.Success(reply);
        var payload = ServerProcess.Payload(reply);
        Assert.Equal(SearchTool.TruncationReasonSearchMaxDirectories, payload.GetProperty("truncation_reason").GetString());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        // The root is the one visited directory: its own file is searched, the pending
        // subdirectories are not entered.
        var match = Assert.Single(payload.GetProperty("matches").EnumerateArray());
        Assert.Equal("root.txt", match.GetProperty("path").GetString());
    }

    // ---- The counters themselves stop the work at the cap (asserted through the seams) ----

    [Fact, Trait("Status", "Baseline")]
    public async Task FileCapStopsTraversalAfterTheBudgetedFiles()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 5; i++)
        {
            sandbox.Write($"file{i}.txt", "needle");
        }

        var visitedDirectories = 0;
        var searchedFiles = 0;
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace), new ResourceBudget { SearchMaxFiles = 2 })
        {
            BeforeDirectoryVisited = _ => visitedDirectories++,
            BeforeFileSearch = _ => searchedFiles++
        };

        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));

        Assert.Equal(2, searchedFiles);
        // The file cap stops the whole traversal, not only the current directory.
        Assert.Equal(1, visitedDirectories);
        Assert.Equal(2, payload.GetProperty("matches").GetArrayLength());
        Assert.Equal(SearchTool.TruncationReasonSearchMaxFiles, payload.GetProperty("truncation_reason").GetString());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task DirectoryCapStopsTraversalAfterTheBudgetedDirectories()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("root.txt", "needle root");
        sandbox.Write("subA/a.txt", "needle a");
        sandbox.Write("subB/b.txt", "needle b");
        sandbox.Write("subC/c.txt", "needle c");

        var visitedDirectories = 0;
        var searchedFiles = 0;
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace), new ResourceBudget { SearchMaxDirectories = 2 })
        {
            BeforeDirectoryVisited = _ => visitedDirectories++,
            BeforeFileSearch = _ => searchedFiles++
        };

        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));

        // The root plus exactly one subdirectory: the directory that consumes the last
        // slot is still processed, and no further directory is entered.
        Assert.Equal(2, visitedDirectories);
        Assert.Equal(2, searchedFiles);
        Assert.Equal(2, payload.GetProperty("matches").GetArrayLength());
        Assert.Equal(SearchTool.TruncationReasonSearchMaxDirectories, payload.GetProperty("truncation_reason").GetString());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
    }

    // ---- Response-character budget: whole items are dropped, JSON is never cut ----

    [Fact, Trait("Status", "Baseline")]
    public async Task ResponseBudgetTrimsMatchesAndReportsMaxResponseChars()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("many.txt", string.Join("\n", Enumerable.Repeat("needle", 20)));
        var policy = new PathPolicy(sandbox.Workspace);
        var arguments = ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" });

        var full = await new SearchTool(policy).ExecuteAsync(arguments, default);
        Assert.True(full.Length > 200, "The fixture must not fit the forced budget.");

        var tool = new SearchTool(policy) { MaxOutputCharsOverride = 200 };
        var raw = await tool.ExecuteAsync(arguments, default);

        Assert.True(raw.Length <= 200, $"Payload exceeded the budget: {raw.Length} characters.");
        var payload = ServerProcess.JsonDocumentParse(raw);
        Assert.Equal(SearchTool.TruncationReasonMaxResponseChars, payload.GetProperty("truncation_reason").GetString());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
        Assert.InRange(payload.GetProperty("matches").GetArrayLength(), 1, 19);
        // Every emitted match is still a complete object with the FS-01/FS-04 fields.
        Assert.All(payload.GetProperty("matches").EnumerateArray(), match =>
        {
            Assert.False(string.IsNullOrEmpty(match.GetProperty("path").GetString()));
            Assert.True(match.GetProperty("line").GetInt32() >= 1);
        });
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ResponseBudgetBelowTheContractEnvelopeStillEmitsValidJson()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("many.txt", string.Join("\n", Enumerable.Repeat("needle", 20)));

        // 64 characters fits the full report; below its 60-ish length only the `truncated`
        // flag survives. Either way the payload is valid JSON that says it is partial — it
        // never degrades to a silent `{}` that a client could read as a complete result.
        var raw = await new SearchTool(new PathPolicy(sandbox.Workspace)) { MaxOutputCharsOverride = 64 }
            .ExecuteAsync(ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default);

        Assert.True(raw.Length <= 64, $"Payload exceeded the budget: {raw.Length} characters.");
        var payload = ServerProcess.JsonDocumentParse(raw);
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(SearchTool.TruncationReasonMaxResponseChars, payload.GetProperty("truncation_reason").GetString());

        // Floor: a budget the report itself cannot fit. The tool emits the report anyway —
        // the transport replaces an over-budget frame and carries the cause out of this very
        // field — so the client must still learn WHY the result is partial. A bare
        // `{"truncated":true}` would say only that it is.
        var tiny = await new SearchTool(new PathPolicy(sandbox.Workspace)) { MaxOutputCharsOverride = 10 }
            .ExecuteAsync(ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default);
        var tinyPayload = ServerProcess.JsonDocumentParse(tiny);
        Assert.Equal(JsonValueKind.Object, tinyPayload.ValueKind);
        Assert.True(tinyPayload.GetProperty("truncated").GetBoolean(),
            "The payload floor must still declare truncation: " + tiny);
        Assert.Equal(SearchTool.TruncationReasonMaxResponseChars,
            tinyPayload.GetProperty("truncation_reason").GetString());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task AnAlreadyTruncatedResultKeepsItsCauseWhenThePayloadIsTrimmed()
    {        using var sandbox = new Sandbox();
        WriteSearchWorkload(sandbox, 400);

        // The deadline cuts the search first; the tiny budget then bounds the answer, and the
        // JSON escaping of the payload inside the frame can push the frame itself over the
        // budget. Either shape is acceptable — a partial result that still says why, or the
        // transport's bounded refusal — but the deadline must survive both. Reporting
        // "max_response_chars" instead would tell the client to narrow a request that was
        // already cut for another reason.
        await using var server = await ServerProcess.StartAsync(
            sandbox.Workspace, ["--operationTimeoutMs=200", "--maxResponseChars=200"]);

        var reply = await server.ToolAsync("search", new { regex = "unlikely_pattern_zzz", file_mask = "*.txt" });
        var reason = McpAssert.TruncationReason(reply);

        Assert.Equal(SearchTool.TruncationReasonOperationTimeout, reason);
    }

    // ---- Deadline versus peer cancellation ----

    [Fact, Trait("Status", "Baseline")]
    public async Task DeadlineCancellationReturnsPartialResultWithOperationTimeout()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 4; i++)
        {
            sandbox.Write($"file{i}.txt", "needle");
        }

        using var deadline = new CancellationTokenSource();
        var recorded = 0;
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            // The transport links peer cancellation and the FS-07 deadline into one token,
            // so a test classifies this cancellation explicitly instead of sleeping.
            IsDeadlineExceeded = _ => true,
            MatchRecorded = _ =>
            {
                if (++recorded == 2)
                {
                    deadline.Cancel();
                }
            }
        };

        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), deadline.Token));

        Assert.Equal(SearchTool.TruncationReasonOperationTimeout, payload.GetProperty("truncation_reason").GetString());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        // The matches recorded before the deadline are returned, not discarded.
        Assert.Equal(2, payload.GetProperty("matches").GetArrayLength());
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task PeerCancellationSurfacesAsOperationCanceledException()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 4; i++)
        {
            sandbox.Write($"file{i}.txt", "needle");
        }

        using var peer = new CancellationTokenSource();
        var recorded = 0;
        // No IsDeadlineExceeded seam: this is the production discriminator. A cancellation
        // observed long before the 30-second default deadline is the peer's, not the budget's.
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            MatchRecorded = _ =>
            {
                if (++recorded == 2)
                {
                    peer.Cancel();
                }
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), peer.Token));
    }

    // ---- list_directory output budget ----

    [Fact, Trait("Status", "Baseline")]
    public async Task ListDirectoryKeepsTheAcceptedShapeWhenNothingIsTruncated()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("b.txt", "x");
        sandbox.Write("a.txt", "x");
        Directory.CreateDirectory(Path.Combine(sandbox.Workspace, "nested"));

        var raw = await new ListDirectoryTool(sandbox.Workspace)
            .ExecuteAsync(ServerProcess.Arguments(new { path = "." }), default);

        var payload = ServerProcess.JsonDocumentParse(raw);
        var entries = payload.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(new[] { "a.txt", "b.txt", "nested" }, entries.Select(entry => entry.GetProperty("name").GetString()));
        Assert.Equal("file", entries[0].GetProperty("type").GetString());
        Assert.Equal("directory", entries[2].GetProperty("type").GetString());
        // The truncation metadata appears only when something was actually dropped.
        Assert.False(payload.TryGetProperty("truncated", out _));
        Assert.False(payload.TryGetProperty("truncation_reason", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task ListDirectoryResponseStaysInsideTheBudgetAndReportsTruncation()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 40; i++)
        {
            sandbox.Write($"entry-{i:D2}.txt", "x");
        }

        var arguments = ServerProcess.Arguments(new { path = "." });
        var full = await new ListDirectoryTool(sandbox.Workspace).ExecuteAsync(arguments, default);
        Assert.True(full.Length > 200, "The fixture must not fit the forced budget.");

        var raw = await new ListDirectoryTool(sandbox.Workspace) { MaxOutputCharsOverride = 200 }
            .ExecuteAsync(arguments, default);

        Assert.True(raw.Length <= 200, $"Payload exceeded the budget: {raw.Length} characters. RAW={raw}");
        var payload = ServerProcess.JsonDocumentParse(raw);
        Assert.True(payload.TryGetProperty("entries", out var entriesNode), "RAW=" + raw);
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(ListDirectoryTool.TruncationReasonMaxResponseChars, payload.GetProperty("truncation_reason").GetString());
        Assert.InRange(payload.GetProperty("entries").GetArrayLength(), 1, 39);
        Assert.All(payload.GetProperty("entries").EnumerateArray(), entry =>
        {
            Assert.False(string.IsNullOrEmpty(entry.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrEmpty(entry.GetProperty("type").GetString()));
        });
    }

    // ---- Regression guards for the accepted FS-01/FS-04 behaviour ----

    [Fact, Trait("Status", "Baseline")]
    public async Task CleanSearchResultIsNotTruncatedAndNotIncomplete()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("root.txt", "needle root");
        sandbox.Write("sub/nested.txt", "needle nested");

        var visitedDirectories = 0;
        var searchedFiles = 0;
        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            BeforeDirectoryVisited = _ => visitedDirectories++,
            BeforeFileSearch = _ => searchedFiles++
        };

        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));

        Assert.Equal(2, visitedDirectories);
        Assert.Equal(2, searchedFiles);
        Assert.Equal(2, payload.GetProperty("matches").GetArrayLength());
        Assert.False(payload.GetProperty("truncated").GetBoolean());
        Assert.False(payload.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
        Assert.False(payload.TryGetProperty("truncation_reason", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task MatchCapStillMarksTruncatedWithoutIncomplete()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("many.txt", string.Join("\n", Enumerable.Repeat("needle", 60)));

        var payload = ServerProcess.JsonDocumentParse(await new SearchTool(new PathPolicy(sandbox.Workspace)).ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));

        // The pre-existing 50-match cap keeps its FS-04 meaning: truncated, not incomplete,
        // and it is not one of the FS-07 budget causes.
        Assert.Equal(50, payload.GetProperty("matches").GetArrayLength());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.False(payload.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0, payload.GetProperty("skipped_count").GetInt32());
        Assert.False(payload.TryGetProperty("truncation_reason", out _));
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task SkippedDetailsStayBoundedWithCompleteCountAndIncomplete()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("accessible.txt", "needle");
        var vanished = new List<string>();
        for (var i = 0; i < 55; i++)
        {
            var directory = Path.Combine(sandbox.Workspace, $"gone-{i:D2}");
            Directory.CreateDirectory(directory);
            vanished.Add(directory);
        }

        var tool = new SearchTool(new PathPolicy(sandbox.Workspace))
        {
            BeforeEntryKindProbe = path =>
            {
                if (vanished.Contains(path, PathPolicy.Comparer) && Directory.Exists(path))
                {
                    Directory.Delete(path);
                }
            }
        };

        var payload = ServerProcess.JsonDocumentParse(await tool.ExecuteAsync(
            ServerProcess.Arguments(new { regex = "needle", file_mask = "*.txt" }), default));

        Assert.Contains(payload.GetProperty("matches").EnumerateArray(), match => match.GetProperty("path").GetString() == "accessible.txt");
        // skipped_count is the full total even though the detail list is bounded at 50.
        Assert.Equal(55, payload.GetProperty("skipped_count").GetInt32());
        Assert.Equal(50, payload.GetProperty("skipped").GetArrayLength());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        Assert.False(payload.GetProperty("truncated").GetBoolean());
        Assert.All(payload.GetProperty("skipped").EnumerateArray(), item =>
        {
            Assert.Equal(item.GetProperty("reason").GetString(), item.GetProperty("code").GetString());
            Assert.Equal("file_not_found", item.GetProperty("reason").GetString());
        });
    }

    [Fact, Trait("Status", "Baseline")]
    public async Task PathologicalRegexStillMapsToResourceLimit()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("input.txt", new string('a', 32) + "!");

        var error = await Assert.ThrowsAsync<OperationalException>(() => new SearchTool(sandbox.Workspace).ExecuteAsync(
            ServerProcess.Arguments(new { regex = "(a+)+$", file_mask = "*.txt" }), default));

        Assert.Equal(ToolErrorCodes.ResourceLimit, error.Code);
    }

    // ---- Deadline versus peer cancel through the production discriminator -----------------

    /// <summary>
    /// A peer <c>notifications/cancelled</c> that arrives while a search is running is the
    /// FS-05 <c>cancelled</c> outcome, never a successful partial result that claims the
    /// operation budget fired. <c>SearchTool.IsDeadlineExceeded</c> is deliberately left
    /// unset, so the classification under test is the production one (the token the
    /// transport actually scheduled); the in-process case above can only prove that a
    /// cancellation is observable, not that its cause is read correctly.
    /// </summary>
    /// <remarks>
    /// The cancel lands late inside a long budget, and both halves of that choice matter for
    /// load-independence. A generous budget (30 s) means no amount of scheduler pressure can
    /// let the deadline fire first and legitimately claim <c>operation_timeout</c> — an
    /// earlier version of this fixture used a 1.5 s budget with the cancel 200 ms before it
    /// and went red in a full parallel run, because under load the search outlived its own
    /// deadline and the timeout answer was correct. The workload is large enough (2000 files)
    /// that the search is still running when the cancel arrives, so the cancel is what the
    /// reply reports. No assertion depends on how long anything took; only the reply does.
    /// </remarks>
    [Fact, Trait("Status", "Baseline")]
    public async Task PeerCancellationLateInTheBudgetIsReportedAsCancelledNotAsATimeoutPartialResult()
    {
        using var sandbox = new Sandbox();
        WriteSearchWorkload(sandbox, 2000);
        const long deadlineMilliseconds = 30_000;
        const int cancelDelayMilliseconds = 1500;
        await using var server = await ServerProcess.StartAsync(
            sandbox.Workspace, [$"--operationTimeoutMs={deadlineMilliseconds}"]);

        var id = await server.SendToolAsync("search", new { regex = "unlikely_pattern_zzz", file_mask = "*.txt" });
        await Task.Delay(cancelDelayMilliseconds);
        await server.NotifyAsync("notifications/cancelled", new { requestId = id });

        var response = await server.ReadResponseAsync(id, TimeSpan.FromSeconds(60));

        // A tool result either way, so the cause is readable off the reply itself: the
        // partial-result shape below is exactly the regression this test exists for.
        Assert.True(response.TryGetProperty("result", out var result),
            "A peer cancellation must be answered with a tool result, not a protocol error: " + response);
        Assert.True(result.GetProperty("isError").GetBoolean(),
            $"A peer cancellation was reported as a successful partial result: {response} "
            + $"(the cancel was sent {cancelDelayMilliseconds} ms into a {deadlineMilliseconds} ms budget)");
        McpAssert.ToolError(response, "cancelled");
        Assert.False(ServerProcess.Payload(response).TryGetProperty("truncation_reason", out _),
            "A cancelled search must not report a truncation reason: " + response);

        // The session is unharmed and the tool is not left holding its slot.
        Assert.True((await server.CallAsync("ping", new { })).TryGetProperty("result", out _));
    }

    /// <summary>
    /// The positive direction of the same discriminator, without any seam and without a
    /// wall-clock assertion: a search that genuinely outlives
    /// <c>--operationTimeoutMs</c> still ends as the bounded partial result with
    /// <c>truncation_reason:"operation_timeout"</c> and <c>isError:false</c>.
    /// </summary>
    /// <remarks>
    /// The budget has to expire well after the request has actually entered the traversal.
    /// A very short budget (this fixture used 100 ms) is not load-independent: under parallel
    /// load the deadline could fire before the search had begun scanning, and the token then
    /// surfaces through the cancellation check on entry — a legitimate
    /// <c>resource_limit</c> refusal that has nothing to do with the partial-result path this
    /// test exists for. Two seconds against a workload that needs several seconds keeps the
    /// deadline dominant while leaving ample room for scheduling jitter.
    /// </remarks>
    [Fact, Trait("Status", "Baseline")]
    public async Task SearchThatGenuinelyExceedsTheOperationTimeoutReturnsAPartialResult()
    {
        using var sandbox = new Sandbox();
        // The traversal needs several seconds; the budget expires after two.
        WriteSearchWorkload(sandbox, 2000);
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace, ["--operationTimeoutMs=2000"]);

        var reply = await server.ToolAsync("search", new { regex = "unlikely_pattern_zzz", file_mask = "*.txt" });

        McpAssert.Success(reply);
        var payload = ServerProcess.Payload(reply);
        Assert.Equal(SearchTool.TruncationReasonOperationTimeout, payload.GetProperty("truncation_reason").GetString());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.True(payload.GetProperty("incomplete").GetBoolean());
        // Nothing matches the probe pattern, so a search that had run to the end would have
        // reported truncated=false and no truncation_reason: the shape above can only come
        // from the fired deadline.
        Assert.Equal(0, payload.GetProperty("matches").GetArrayLength());
    }

    /// <summary>
    /// A search workload of <paramref name="fileCount"/> small files, none of which contains
    /// the probe pattern. The search therefore has to visit every file instead of stopping at
    /// the 50-match cap, and a traversal costs roughly two milliseconds per file (open,
    /// binary probe, decode, match), which is what lets a search outlive a short operation
    /// budget on any machine this suite runs on.
    /// </summary>
    private static void WriteSearchWorkload(Sandbox sandbox, int fileCount)
    {
        var content = string.Join('\n', Enumerable.Range(0, 12).Select(line => $"line {line} filler text without the probe pattern"));
        for (var index = 0; index < fileCount; index++)
        {
            sandbox.Write($"work/file{index:D4}.txt", content);
        }
    }
}
