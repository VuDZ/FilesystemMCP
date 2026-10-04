using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

/// <summary>
/// FS-08 acceptance matrix. The installer must merge into an existing OpenCode configuration
/// instead of replacing it, must prepare and validate both targets before publishing either,
/// and must report what it changed, which bytes it preserved and where it put them.
/// Every case runs <c>install2opencode.ps1</c> out of process inside its own <see cref="Sandbox"/>;
/// nothing here reads or writes a real user configuration.
/// </summary>
[Trait("Spec", "FS-08")]
[Trait("Status", "Baseline")]
public sealed class InstallerTests
{
    private const string ConfigName = "opencode.json";
    private const string AgentsName = "AGENTS.md";
    private const string SchemaUrl = "https://opencode.ai/config.json";
    private const string SymLinkOption = "--allowSymLinks=false";
    private const string DenySymLinks = "-AllowSymLinks:false";
    private const string AllowSymLinksTrue = "-AllowSymLinks:true";
    private const string BackupPattern = ConfigName + ".filesystemmcp-backup-";
    private const string TempPattern = ConfigName + ".filesystemmcp-";

    /// <summary>
    /// The installer shipped next to the tests. <c>FILESYSTEM_MCP_TEST_INSTALLER</c> points the same
    /// matrix at another revision of the script (with its own AGENTS.md.sample beside it), the way
    /// <c>FILESYSTEM_MCP_TEST_SERVER</c> and <c>FILESYSTEM_MCP_TEST_POWERSHELL</c> already select
    /// their target; without the variable nothing changes.
    /// </summary>
    private static string ScriptPath => Environment.GetEnvironmentVariable("FILESYSTEM_MCP_TEST_INSTALLER")
        ?? Path.Combine(AppContext.BaseDirectory, "assets", "install2opencode.ps1");
    private static string SamplePath => Path.Combine(Path.GetDirectoryName(ScriptPath)!, "AGENTS.md.sample");

    /// <summary>The sample's opening line is the marker that must appear exactly once.</summary>
    private static string SampleMarker => File.ReadLines(SamplePath).First();

    private static string SampleText => File.ReadAllText(SamplePath);

    /// <summary>Header that marks the section appended to an AGENTS.md that already had content.</summary>
    private const string AgentsSectionHeader = "# FilesystemMCP Agent Rules";

    // -------------------------------------------------------------- fresh install ---------------

    [Fact]
    public async Task FreshInstallUsesUnicodeWorkspaceAsSingleArgument()
    {
        using var sandbox = new Sandbox();
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var config = await ConfigAsync(sandbox);
        Assert.Equal("local", config.GetProperty("mcp").GetProperty("filesystem-mcp").GetProperty("type").GetString());
        AssertCommand(sandbox, Command(config), denySymLinks: false);
        Assert.Equal(SchemaUrl, config.GetProperty("$schema").GetString());
        Assert.True(result.Stdout.Contains(ConfigName, StringComparison.Ordinal), "stdout does not report the config: " + result.Stdout);
        Assert.Equal("created", AgentsStatus(result));
        var agents = Normalize(await File.ReadAllTextAsync(AgentsPath(sandbox)));
        Assert.Equal(Normalize(SampleText.TrimEnd()) + "\n", agents);
        AssertNoBackupReported(result);
        Assert.Empty(Backups(sandbox));
        Assert.Empty(TempFiles(sandbox));
    }

    [Fact]
    public async Task ExistingModelServersAndUnknownSettingsArePreserved()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.Write(ConfigName, """
            {"model":"existing-model","theme":"dark","custom":{"flag":true},"mcp":{"existing-server":{"type":"local","command":["old","--flag"],"enabled":false}}}
            """);
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var config = ServerProcess.JsonDocumentParse(await File.ReadAllTextAsync(path));
        Assert.True(config.TryGetProperty("model", out var model), "Installer removed model setting.");
        Assert.Equal("existing-model", model.GetString());
        Assert.Equal("dark", config.GetProperty("theme").GetString());
        Assert.True(config.GetProperty("custom").GetProperty("flag").GetBoolean());
        var other = config.GetProperty("mcp").GetProperty("existing-server");
        Assert.Equal("old", other.GetProperty("command")[0].GetString());
        Assert.Equal("--flag", other.GetProperty("command")[1].GetString());
        Assert.Equal("local", other.GetProperty("type").GetString());
        Assert.False(other.GetProperty("enabled").GetBoolean());
        Assert.Equal(SchemaUrl, config.GetProperty("$schema").GetString());
        AssertCommand(sandbox, Command(config), denySymLinks: false);
    }

    [Fact]
    public async Task ExistingFilesystemMcpEntryKeepsExtraFieldsAndUpdatesTypeAndCommand()
    {
        using var sandbox = new Sandbox();
        sandbox.Write(ConfigName, """
            {"mcp":{"filesystem-mcp":{"type":"remote","command":["stale-binary"],"enabled":false,"environment":{"MCP_MODE":"strict"}},"other":{"type":"local","command":["keep-me"]}}}
            """);
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var config = await ConfigAsync(sandbox);
        var server = config.GetProperty("mcp").GetProperty("filesystem-mcp");
        Assert.Equal("local", server.GetProperty("type").GetString());
        AssertCommand(sandbox, Command(config), denySymLinks: false);
        Assert.False(server.GetProperty("enabled").GetBoolean());
        Assert.Equal("strict", server.GetProperty("environment").GetProperty("MCP_MODE").GetString());
        Assert.Equal("keep-me", config.GetProperty("mcp").GetProperty("other").GetProperty("command")[0].GetString());
    }

    [Theory]
    [InlineData("{\"model\":\"m\"}", SchemaUrl)]
    [InlineData("{\"$schema\":\"https://example.test/custom.json\",\"model\":\"m\"}", "https://example.test/custom.json")]
    public async Task SchemaIsAddedOnlyWhenAbsentAndNeverOverwritten(string fixture, string expectedSchema)
    {
        using var sandbox = new Sandbox();
        sandbox.Write(ConfigName, fixture);
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var config = await ConfigAsync(sandbox);
        Assert.Equal(expectedSchema, config.GetProperty("$schema").GetString());
        Assert.Equal("m", config.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SecondIdenticalRunWritesNothingAndReportsNoBackup()
    {
        using var sandbox = new Sandbox();
        AssertSucceeded(await InstallAsync(sandbox));
        var configBytes = await File.ReadAllBytesAsync(ConfigPath(sandbox));
        var agentsBytes = await File.ReadAllBytesAsync(AgentsPath(sandbox));
        var entries = EntryNames(sandbox);

        var second = await InstallAsync(sandbox);
        AssertSucceeded(second);
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Equal(agentsBytes, await File.ReadAllBytesAsync(AgentsPath(sandbox)));
        Assert.Equal(entries, EntryNames(sandbox));
        Assert.Equal("unchanged", AgentsStatus(second));
        AssertNoBackupReported(second);
        Assert.Empty(Backups(sandbox));
    }

    [Fact]
    public async Task ConfigThatAlreadyMatchesIsNotRewrittenWhileAgentsIsStillPublished()
    {
        using var sandbox = new Sandbox();
        AssertSucceeded(await InstallAsync(sandbox));
        var configBytes = await File.ReadAllBytesAsync(ConfigPath(sandbox));
        File.Delete(AgentsPath(sandbox));

        var second = await InstallAsync(sandbox);
        AssertSucceeded(second);
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Equal("created", AgentsStatus(second));
        AssertNoBackupReported(second);
        Assert.Empty(Backups(sandbox));
    }

    [Fact]
    public async Task AsJsonEmitsOneStrictJsonLineWithoutTurningTheRunIntoADryRun()
    {
        using var sandbox = new Sandbox();
        var result = await InstallAsync(sandbox, "-AsJson");
        AssertSucceeded(result);

        var summary = result.Stdout.Trim();
        Assert.False(summary.Contains('\n'), "the JSON summary is not a single line: " + summary);
        Assert.Equal(JsonValueKind.Object, ServerProcess.JsonDocumentParse(summary).ValueKind);
        Assert.True(File.Exists(ConfigPath(sandbox)), "-AsJson suppressed the install.");
        Assert.True(File.Exists(AgentsPath(sandbox)), "-AsJson suppressed the AGENTS.md install.");
    }

    /// <summary>
    /// The same run in both report modes. The JSON summary may carry no path where nothing was
    /// preserved, and the human line must still be printable and must not point at a real file.
    /// </summary>
    [Fact]
    public async Task AsJsonReportsNoBackupPathWhereNothingWasPreserved()
    {
        using var sandbox = new Sandbox();
        AssertSucceeded(await InstallAsync(sandbox));

        var json = await InstallAsync(sandbox, "-AsJson");
        AssertSucceeded(json);
        var summary = ServerProcess.JsonDocumentParse(json.Stdout.Trim());
        Assert.Equal(0, summary.GetProperty("backupCount").GetInt32());
        var backup = summary.GetProperty("backup").GetString();
        Assert.True(string.IsNullOrEmpty(backup), "a non-mutating run named a backup: " + backup);
        Assert.Equal("none", summary.GetProperty("recovery").GetString());

        var human = await InstallAsync(sandbox);
        AssertSucceeded(human);
        AssertNoBackupReported(human);
        Assert.Empty(Backups(sandbox));
    }

    // ------------------------------------------------------------------- backups -----------------

    [Fact]
    public async Task EveryMutatingRunKeepsADistinctByteExactBackupOfWhatItReplaced()
    {
        using var sandbox = new Sandbox();
        const string fixture = "{\r\n  \"model\": \"модель с пробелами\",\r\n  \"mcp\": {\"filesystem-mcp\": {\"type\": \"remote\", \"command\": [\"stale\"]}}\r\n}\r\n";
        var first = await WriteConfigAsync(sandbox, fixture);

        var firstRun = await InstallAsync(sandbox);
        AssertSucceeded(firstRun);
        Assert.Single(Backups(sandbox));
        var firstBackup = Path.GetFullPath(ReportValue(firstRun, "Backup:"));
        Assert.True(File.Exists(firstBackup), "the reported backup does not exist: " + firstBackup);
        Assert.Equal(first, await File.ReadAllBytesAsync(firstBackup));

        var second = await WriteConfigAsync(sandbox, """
            {"model":"второй","mcp":{"filesystem-mcp":{"type":"remote","command":["stale"]}}}
            """);
        var secondRun = await InstallAsync(sandbox);
        AssertSucceeded(secondRun);
        Assert.Equal(2, Backups(sandbox).Length);
        var secondBackup = Path.GetFullPath(ReportValue(secondRun, "Backup:"));
        Assert.False(string.Equals(firstBackup, secondBackup, StringComparison.OrdinalIgnoreCase),
            "the second run reused the backup of the first one: " + secondBackup);
        Assert.Equal(second, await File.ReadAllBytesAsync(secondBackup));
        Assert.Equal(first, await File.ReadAllBytesAsync(firstBackup));
        Assert.NotEqual(second, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
    }

    // ------------------------------------------------------------------ refusals -----------------

    [Fact]
    public async Task InvalidJsonIsNotOverwritten()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{ invalid existing config");
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    [Fact]
    public async Task JsoncWithCommentsIsRefused()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, """
            {
              // JSONC is expected in this backlog item, not accepted by the installer
              "mcp": { "filesystem-mcp": { "type": "remote", "command": ["stale"] } }
            }
            """);
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    [Theory]
    [InlineData("{\"mcp\":[]}")]
    [InlineData("{\"mcp\":\"filesystem-mcp\"}")]
    [InlineData("{\"mcp\":5}")]
    [InlineData("{\"mcp\":true}")]
    public async Task NonObjectMcpIsRefused(string fixture)
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, fixture);
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    /// <summary>
    /// A root of <c>null</c> or <c>[]</c> parses to the same <c>$null</c> an absent file produces, so
    /// only "the file exists" tells them apart. Merging over one would discard the whole document
    /// while reporting success.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task NonObjectRootIsRefusedInsteadOfReplaced(string fixture)
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, fixture);
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    /// <summary>
    /// PowerShell cannot read back a document whose members differ only in case:
    /// <c>ConvertFrom-Json</c> refuses it and the PSCustomObject cast inside <c>ConvertTo-Json</c>
    /// merges the keys. Such a config is refused rather than written with settings dropped. Names
    /// that merely differ in case from nothing (for example <c>MCPNote</c>) are ordinary settings.
    /// </summary>
    [Theory]
    [InlineData("{\"MCP\":{\"other\":{\"command\":[\"keep\"]}},\"model\":\"m\"}")]
    [InlineData("{\"mcp\":{\"FileSystem-MCP\":{\"command\":[\"keep\"]}}}")]
    [InlineData("{\"$Schema\":\"https://custom.test/x.json\",\"$schema\":\"https://other.test/y.json\"}")]
    public async Task MembersDifferingOnlyInCaseAreRefused(string fixture)
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, fixture);
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    [Fact]
    public async Task CaseDifferentMemberNamesThatDoNotCollideArePreserved()
    {
        using var sandbox = new Sandbox();
        await WriteConfigAsync(sandbox, "{\"MCPNote\":\"keep me\",\"Model\":\"m\",\"mcp\":{}}");
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var config = await ConfigAsync(sandbox);
        Assert.Equal("keep me", config.GetProperty("MCPNote").GetString());
        Assert.Equal("m", config.GetProperty("Model").GetString());
        Assert.True(config.GetProperty("mcp").TryGetProperty("filesystem-mcp", out _));
    }

    /// <summary>
    /// Inside the server, our fields are named exactly <c>type</c> and <c>command</c>. A server that
    /// already carries <c>Type</c> or <c>Command</c> would end up with both spellings, which
    /// PowerShell cannot read back, so it is refused untouched; a case difference that collides
    /// with nothing is an ordinary setting and survives.
    /// </summary>
    [Fact]
    public async Task CollidingTypeOrCommandInsideTheServerIsRefusedUntouched()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox,
            "{\"mcp\":{\"filesystem-mcp\":{\"Type\":\"custom\",\"Command\":[\"--keep-me\"],\"Enabled\":true}}}");
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    [Fact]
    public async Task CaseDifferentServerFieldsThatDoNotCollideArePreserved()
    {
        using var sandbox = new Sandbox();
        await WriteConfigAsync(sandbox,
            "{\"mcp\":{\"filesystem-mcp\":{\"type\":\"local\",\"command\":[\"a\"],\"Enabled\":true,\"Extra\":{\"k\":1}}}}");
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var text = await File.ReadAllTextAsync(ConfigPath(sandbox));
        Assert.Contains("\"Enabled\":true", text, StringComparison.Ordinal);
        Assert.Contains("\"Extra\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A JSON reader keeps only one value of a duplicated member, so the installer cannot know which
    /// one the author meant: the document is refused before anything is written. The check walks
    /// nested objects and arrays, where the same loss happens one level down.
    /// </summary>
    [Theory]
    [InlineData("{\"model\":\"first\",\"model\":\"second\",\"mcp\":{}}")]
    [InlineData("{\"mcp\":{\"filesystem-mcp\":{\"type\":\"local\",\"command\":[\"a\"],\"env\":{\"A\":\"1\",\"A\":\"2\"}}}}")]
    [InlineData("{\"outer\":{\"inner\":{\"k\":1,\"k\":2}},\"model\":\"m\"}")]
    [InlineData("{\"arr\":[{\"x\":1,\"x\":2}],\"model\":\"m\"}")]
    [InlineData("{\"mcp\":{\"a\":{\"deep\":{\"deeper\":{\"z\":1,\"z\":2}}}}}")]
    public async Task DuplicateObjectMembersAreRefused(string fixture)
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, fixture);
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    /// <summary>
    /// The server slot is ours to write, not to replace with something else: an array, string,
    /// number or boolean in <c>mcp.filesystem-mcp</c> is the author's setting and is refused, while
    /// an explicit null means "unset" and is rewritten as a fresh server.
    /// </summary>
    [Theory]
    [InlineData("{\"mcp\":{\"filesystem-mcp\":[1,2]},\"model\":\"m\"}")]
    [InlineData("{\"mcp\":{\"filesystem-mcp\":\"a string\"},\"model\":\"m\"}")]
    [InlineData("{\"mcp\":{\"filesystem-mcp\":42},\"model\":\"m\"}")]
    [InlineData("{\"mcp\":{\"filesystem-mcp\":true},\"model\":\"m\"}")]
    public async Task NonObjectServerSlotIsRefused(string fixture)
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, fixture);
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    [Fact]
    public async Task NullServerSlotMeansUnsetAndIsRewritten()
    {
        using var sandbox = new Sandbox();
        await WriteConfigAsync(sandbox, "{\"mcp\":{\"filesystem-mcp\":null,\"other\":{\"command\":[\"keep\"]}},\"model\":\"m\"}");
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var config = await ConfigAsync(sandbox);
        Assert.Equal("keep", config.GetProperty("mcp").GetProperty("other").GetProperty("command")[0].GetString());
        AssertCommand(sandbox, Command(config), denySymLinks: false);
    }

    /// <summary>
    /// A failure between the backup copy and the hook's return must not leave an orphan backup: a
    /// <c>.bak</c> file nobody was told about would contradict "a backup exists only when this run
    /// replaced the file", and the next run would add a second one beside it. The fault is injected
    /// into a copy of the script, because a real one cannot be produced from outside.
    /// </summary>
    [Fact]
    public async Task FailureAfterTheBackupCopyLeavesNoOrphanBackup()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox,
            "{\"model\":\"keep-me\",\"mcp\":{\"filesystem-mcp\":{\"type\":\"local\",\"command\":[\"old\"]}}}");

        var scripts = Path.Combine(sandbox.Root, "scripts");
        Directory.CreateDirectory(scripts);
        var broken = Path.Combine(scripts, "install2opencode.ps1");
        var text = await File.ReadAllTextAsync(ScriptPath);
        var anchor = "$attributes = [System.IO.File]::GetAttributes($Path)";
        Assert.Contains(anchor, text, StringComparison.Ordinal);
        await File.WriteAllTextAsync(broken,
            text.Replace(anchor, "throw 'injected failure after the backup copy'\n    " + anchor, StringComparison.Ordinal),
            Sandbox.Utf8);
        File.Copy(SamplePath, Path.Combine(scripts, "AGENTS.md.sample"), overwrite: true);

        var result = await ProcessRunner.PowerShellAsync(new[]
        {
            "-File", broken, "-BinaryPath", ServerProcess.DefaultExecutable, "-WorkspacePath", sandbox.Workspace
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Empty(Backups(sandbox));
        Assert.Empty(TempFiles(sandbox));
    }

    /// <summary>
    /// Only a complete option is carried over: a bare <c>--name</c> whose value was a separate
    /// argument is dropped together with that value, because the flag alone makes the server refuse
    /// to start — which is also what the original pair did.
    /// </summary>
    [Fact]
    public async Task ValueLessNamedOptionIsNotCarriedOver()
    {
        using var sandbox = new Sandbox();
        await WriteConfigAsync(sandbox,
            "{\"mcp\":{\"filesystem-mcp\":{\"type\":\"local\",\"command\":"
            + "[\"old.exe\",\"/old\",\"--logDirectory\",\"C:/logs\",\"--maxFileBytes=1024\"]}}}");
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var command = Command(await ConfigAsync(sandbox));
        Assert.Contains("--maxFileBytes=1024", command);
        Assert.DoesNotContain("--logDirectory", command);
        Assert.DoesNotContain("C:/logs", command);
        Assert.DoesNotContain(command, argument => argument.StartsWith("--", StringComparison.Ordinal)
            && !argument.Contains('='));
    }

    /// <summary>
    /// The user's own key order survives the merge: siblings and server fields keep their positions
    /// and only new members are appended. A byte-comparing rewrite is not enough here, so the
    /// assertion checks the index of each key in the written text.
    /// </summary>
    [Fact]
    public async Task ExistingKeyOrderIsPreserved()
    {
        using var sandbox = new Sandbox();
        await WriteConfigAsync(sandbox,
            "{\"mcp\":{\"filesystem-mcp\":{\"type\":\"local\",\"command\":[\"a\"],\"enabled\":true},"
            + "\"zzz-other\":{\"type\":\"local\",\"command\":[\"z\"]}},\"model\":\"m\"}");
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var text = await File.ReadAllTextAsync(ConfigPath(sandbox));
        var mcp = text.IndexOf("\"mcp\":", StringComparison.Ordinal);
        var model = text.IndexOf("\"model\":", StringComparison.Ordinal);
        var server = text.IndexOf("\"filesystem-mcp\":", StringComparison.Ordinal);
        var other = text.IndexOf("\"zzz-other\":", StringComparison.Ordinal);
        var enabled = text.IndexOf("\"enabled\":", StringComparison.Ordinal);
        var type = text.IndexOf("\"type\":", StringComparison.Ordinal);
        Assert.True(mcp >= 0 && model > mcp, "top-level mcp moved: " + text);
        Assert.True(server >= 0 && other > server, "filesystem-mcp moved among its siblings: " + text);
        Assert.True(enabled > 0 && enabled < other, "server fields were reordered: " + text);
        Assert.True(type > 0 && type < enabled, "type was moved after enabled: " + text);
        Assert.True(text.IndexOf("\"$schema\"", StringComparison.Ordinal) > model, "$schema was not appended: " + text);
    }

    /// <summary>An existing AGENTS.md is preserved verbatim, including trailing spaces, of which
    /// only the final line break is normalised.</summary>
    [Fact]
    public async Task ExistingAgentsTextIsPreservedIncludingTrailingSpaces()
    {
        using var sandbox = new Sandbox();
        await WriteConfigAsync(sandbox, "{\"model\":\"m\"}");
        const string original = "user rules   \r\n";
        await File.WriteAllTextAsync(AgentsPath(sandbox), original, Sandbox.Utf8);

        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);
        var text = await File.ReadAllTextAsync(AgentsPath(sandbox));
        Assert.StartsWith(original, text, StringComparison.Ordinal);
        Assert.Contains("# FilesystemMCP Agent Rules", text, StringComparison.Ordinal);
    }

    /// <summary>A dry run still prepares: a plan that cannot be built is a refusal, not a report
    /// that the installation would succeed.</summary>
    [Fact]
    public async Task WhatIfRefusesWhenTheAgentsPlanCannotBePrepared()
    {
        using var sandbox = new Sandbox();
        var bytes = await WriteConfigAsync(sandbox, "{\"model\":\"m\"}");
        await File.WriteAllTextAsync(AgentsPath(sandbox), "# rules\r\n", Sandbox.Utf8);
        ProcessResult result;
        using (File.Open(AgentsPath(sandbox), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await InstallAsync(sandbox, "-WhatIf", "-AsJson");
        }

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        var summary = ServerProcess.JsonDocumentParse(result.Stdout.Trim().Split('\n').Last(line => line.TrimStart().StartsWith('{')).Trim());
        Assert.Equal("failed", summary.GetProperty("agentsAction").GetString());
        Assert.Equal("agents", summary.GetProperty("failed").GetString());
        // The report describes the run that happened: a refused dry run is still a dry run.
        Assert.True(summary.GetProperty("whatIf").GetBoolean(), "a refused -WhatIf run reported whatIf=false");
    }

    [Fact]
    public async Task WhatIfRefusesWhenTheSampleIsMissing()
    {
        using var sandbox = new Sandbox();
        var bytes = await WriteConfigAsync(sandbox, "{\"model\":\"m\"}");
        var scripts = Path.Combine(sandbox.Root, "scripts");
        Directory.CreateDirectory(scripts);
        File.Copy(ScriptPath, Path.Combine(scripts, "install2opencode.ps1"));

        var result = await ProcessRunner.PowerShellAsync(new[]
        {
            "-File", Path.Combine(scripts, "install2opencode.ps1"),
            "-BinaryPath", ServerProcess.DefaultExecutable,
            "-WorkspacePath", sandbox.Workspace,
            "-WhatIf"
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
    }

    /// <summary>Refusals that happen before the config paths are known still answer <c>-AsJson</c>
    /// with one JSON line instead of a bare host message.</summary>
    [Fact]
    public async Task EarlyOptionRefusalIsReportedAsJson()
    {
        using var sandbox = new Sandbox();
        var result = await InstallAsync(sandbox, "-AllowSymLinks", "maybe", "-AsJson");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("refused", Summary(result).GetProperty("configAction").GetString());
        Assert.Equal("options", Summary(result).GetProperty("failed").GetString());
    }

    [Fact]
    public async Task MissingBinaryIsReportedAsJson()
    {
        using var sandbox = new Sandbox();
        var result = await InstallWithBinaryAsync(sandbox, Path.Combine(sandbox.Root, "no-such-binary.exe"), "-AsJson");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("refused", Summary(result).GetProperty("configAction").GetString());
        Assert.Equal("binary", Summary(result).GetProperty("failed").GetString());
    }

    [Fact]
    public async Task ConfigTargetThatIsADirectoryIsReportedAsJsonToo()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(ConfigPath(sandbox));
        var result = await InstallAsync(sandbox, "-AsJson");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("refused", Summary(result).GetProperty("configAction").GetString());
        Assert.Equal(ConfigName, Summary(result).GetProperty("failed").GetString());
    }

    /// <summary>A config that is not valid UTF-8 is not a JSON document; the bytes must not be
    /// replaced with U+FFFD and reported as a successful merge.</summary>
    [Fact]
    public async Task InvalidUtf8InTheConfigIsRefused()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Workspace, ConfigName);
        var bytes = new byte[] { 0x7B, 0x22, 0x6D, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D };
        await File.WriteAllBytesAsync(path, bytes);

        var result = await InstallAsync(sandbox);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Backups(sandbox));
    }

    /// <summary>A number too large to be finite would come back as the string "Infinity" on
    /// PowerShell 7, which changes the type of the user's setting.</summary>
    [Fact]
    public async Task NonFiniteNumberIsRefusedInsteadOfBecomingAString()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"d\":1e999,\"model\":\"m\"}");
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    /// <summary>The opencode.json target being a directory must be refused, not written through.</summary>
    [Fact]
    public async Task ConfigTargetThatIsADirectoryIsRefused()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sandbox.Workspace, ConfigName));
        var result = await InstallAsync(sandbox);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(Path.Combine(sandbox.Workspace, ConfigName)));
        Assert.False(File.Exists(AgentsPath(sandbox)), "a refused run published AGENTS.md");
        Assert.Empty(Backups(sandbox));
    }

    /// <summary>A config that cannot be read at all is a prepare refusal: no raw exception, and a
    /// machine consumer that asked for JSON still receives JSON.</summary>
    [Fact]
    public async Task UnreadableConfigIsRefusedWithAJsonReport()
    {
        using var sandbox = new Sandbox();
        var bytes = await WriteConfigAsync(sandbox, "{\"model\":\"m\"}");
        var path = ConfigPath(sandbox);
        ProcessResult result;
        using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await InstallAsync(sandbox, "-AsJson");
        }

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        var summary = ServerProcess.JsonDocumentParse(result.Stdout.Trim().Split('\n').Last(line => line.TrimStart().StartsWith('{')).Trim());
        Assert.Equal("refused", summary.GetProperty("configAction").GetString());
        Assert.Equal("not-attempted", summary.GetProperty("agentsAction").GetString());
        Assert.Contains("could not be read", summary.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Backups(sandbox));
    }

    /// <summary>
    /// An explicit <c>null</c> is treated as "no member": a JSON null asserts nothing about intent,
    /// and refusing it would reject a configuration whose author meant "unset". The member is
    /// written as a fresh object, and every other setting survives.
    /// </summary>
    [Fact]
    public async Task ExplicitNullMcpMeansAbsentAndIsReplacedByAFreshObject()
    {
        using var sandbox = new Sandbox();
        await WriteConfigAsync(sandbox, "{\"model\":\"m\",\"mcp\":null}");
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var config = await ConfigAsync(sandbox);
        Assert.Equal("m", config.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Object, config.GetProperty("mcp").ValueKind);
        AssertCommand(sandbox, Command(config), denySymLinks: false);
    }

    /// <summary>
    /// A deeply nested unknown setting must survive the merge. <c>ConvertTo-Json</c> replaces
    /// everything below its depth limit with a string and reports that only as a warning, so this
    /// case fails on a truncated write even though the output would still be valid JSON.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    public async Task DeeplyNestedUnknownSettingsSurviveTheMerge(int depth)
    {
        using var sandbox = new Sandbox();
        await WriteConfigAsync(sandbox, NestedConfig(depth));
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);

        var text = await File.ReadAllTextAsync(ConfigPath(sandbox));
        Assert.Contains("\"leaf\":\"kept\"", text, StringComparison.Ordinal);
        // The nesting really is that deep in the written file, not flattened on the way out.
        Assert.Equal(depth, CountOccurrences(text, "\"a\":"));
    }

    /// <summary>
    /// Past the serializer's own limit the installer refuses instead of writing a config that
    /// silently lost settings, and the refusal is clean: original bytes, no backup, no temp file.
    /// </summary>
    [Fact]
    public async Task SettingsTooDeepToSerializeAreRefusedInsteadOfTruncated()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, NestedConfig(120));
        var result = await InstallAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    [Fact]
    public async Task BinaryPathThatIsADirectoryIsRefused()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"model\":\"m\"}");
        var directory = Path.Combine(sandbox.Root, "binary-directory");
        Directory.CreateDirectory(directory);

        var result = await InstallWithBinaryAsync(sandbox, directory);
        await AssertRefusedAsync(sandbox, result, original);
    }

    [Fact]
    public async Task MissingBinaryPathIsRefused()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"model\":\"m\"}");
        var result = await InstallWithBinaryAsync(sandbox, Path.Combine(sandbox.Root, "no-such-binary.exe"));
        await AssertRefusedAsync(sandbox, result, original);
    }

    [Fact]
    public async Task WorkspacePathThatIsAFileIsRefused()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"model\":\"m\"}");
        var file = Path.Combine(sandbox.Root, "workspace-is-a-file.txt");
        await File.WriteAllTextAsync(file, "not a directory");

        var result = await ProcessRunner.PowerShellAsync(new[]
        {
            "-File", ScriptPath, "-BinaryPath", ServerProcess.DefaultExecutable, "-WorkspacePath", file
        });
        await AssertRefusedAsync(sandbox, result, original);
    }

    /// <summary>
    /// A deny ACE on the workspace and on the config itself is the denial no write mechanism can
    /// bypass: creating a backup, creating an atomic-write temp file, writing the config in place
    /// and replacing it by rename all need one of these rights. The self-check below proves the
    /// denial is effective, so a pass can never be vacuous; the finally block always removes both
    /// ACEs again.
    /// </summary>
    [WindowsFact, SupportedOSPlatform("windows")]
    public async Task DeniedWorkspacePermissionIsRefusedWithoutWritingAnything()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"mcp\":{\"filesystem-mcp\":{\"type\":\"remote\",\"command\":[\"stale\"]}}}");
        var directorySecurity = new DirectoryInfo(sandbox.Workspace).GetAccessControl(AccessControlSections.Access);
        var fileSecurity = new FileInfo(ConfigPath(sandbox)).GetAccessControl(AccessControlSections.Access);
        var user = WindowsIdentity.GetCurrent().User!;
        var directoryDeny = new FileSystemAccessRule(user,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.AppendData
                | FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
        var fileDeny = new FileSystemAccessRule(user,
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete, AccessControlType.Deny);
        directorySecurity.AddAccessRule(directoryDeny);
        fileSecurity.AddAccessRule(fileDeny);
        new DirectoryInfo(sandbox.Workspace).SetAccessControl(directorySecurity);
        new FileInfo(ConfigPath(sandbox)).SetAccessControl(fileSecurity);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => File.WriteAllText(Path.Combine(sandbox.Workspace, "deny-probe.tmp"), "x"));
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(ConfigPath(sandbox), "x"));

            var result = await InstallAsync(sandbox);
            await AssertRefusedAsync(sandbox, result, original);
        }
        finally
        {
            directorySecurity.RemoveAccessRuleSpecific(directoryDeny);
            fileSecurity.RemoveAccessRuleSpecific(fileDeny);
            new DirectoryInfo(sandbox.Workspace).SetAccessControl(directorySecurity);
            new FileInfo(ConfigPath(sandbox)).SetAccessControl(fileSecurity);
        }
    }

    /// <summary>
    /// A foreign <c>FileShare.None</c> handle makes the target unreadable and unwritable for the
    /// installer without any ACL work: the run has to refuse instead of assuming an absent config.
    /// </summary>
    [WindowsFact]
    public async Task ConfigLockedAgainstEveryReaderAndWriterIsRefused()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"mcp\":{\"filesystem-mcp\":{\"type\":\"remote\",\"command\":[\"stale\"]}}}");
        ProcessResult result;
        using (new FileStream(ConfigPath(sandbox), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await InstallAsync(sandbox);
        }

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Empty(Backups(sandbox));
        Assert.Empty(TempFiles(sandbox));
        Assert.False(File.Exists(AgentsPath(sandbox)));
    }

    /// <summary>
    /// AGENTS.md that cannot be read at all fails while the run is still planning, so neither
    /// target may be published and no backup or temp file may be left behind.
    /// </summary>
    [WindowsFact]
    public async Task UnreadableAgentsFileIsRefusedBeforeAnythingIsWritten()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"mcp\":{\"filesystem-mcp\":{\"type\":\"remote\",\"command\":[\"stale\"]}}}");
        sandbox.Write(AgentsName, "# Мои правила\n");
        var agentsBytes = await File.ReadAllBytesAsync(AgentsPath(sandbox));

        ProcessResult result;
        using (new FileStream(AgentsPath(sandbox), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await InstallAsync(sandbox);
        }

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Equal(agentsBytes, await File.ReadAllBytesAsync(AgentsPath(sandbox)));
        Assert.Empty(Backups(sandbox));
        Assert.Empty(TempFiles(sandbox));
    }

    // ------------------------------------------------------- failure of the second commit --------

    /// <summary>
    /// A <c>FileShare.Read</c> holder still lets the installer read AGENTS.md while it plans the
    /// change, but no process may write it: the failure therefore lands exactly in the second
    /// commit, after opencode.json has already been replaced. The config must come back byte for
    /// byte, the backup stays as the recovery path, and the outcome must say that a restore
    /// happened - a run that reported "nothing was published" never reached the second commit and
    /// would prove nothing about recovery.
    /// </summary>
    [WindowsFact]
    public Task SecondCommitFailureRestoresTheConfigFromTheBackupAndReportsRecovery() =>
        SecondCommitFailureAsync(ProcessRunner.PowerShellExecutable);

    [PwshFact]
    public Task PwshSecondCommitFailureRestoresTheConfigFromTheBackupAndReportsRecovery() =>
        SecondCommitFailureAsync(PwshFactAttribute.Executable!);

    // ---------------------------------------------------------------- allowSymLinks --------------

    [Fact]
    public async Task DefaultInstallOmitsTheSymLinkOption()
    {
        using var sandbox = new Sandbox();
        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);
        AssertCommand(sandbox, Command(await ConfigAsync(sandbox)), denySymLinks: false);
    }

    /// <summary>
    /// An explicit false has to reach the installer from a scripted or unattended invocation, which
    /// is a <c>-File</c> command line. The literal below is the one PowerShell 7 binds; see
    /// <see cref="PwshDefaultRunRemovesAStaleSymLinkOption"/> for the same transition on PowerShell 7.
    /// </summary>
    [Fact]
    public async Task ExplicitFalseAppendsTheSymLinkOption()
    {
        using var sandbox = new Sandbox();
        var result = await InstallAsync(sandbox, DenySymLinks);
        AssertSucceeded(result);
        AssertCommand(sandbox, Command(await ConfigAsync(sandbox)), denySymLinks: true);
    }

    [Fact]
    public async Task DefaultRunRemovesAStaleSymLinkOptionFromAPreviousInstall()
    {
        using var sandbox = new Sandbox();
        // The space-separated spelling a cmd/CI caller uses; the colon form is covered above.
        AssertSucceeded(await InstallAsync(sandbox, "-AllowSymLinks", "false"));
        AssertCommand(sandbox, Command(await ConfigAsync(sandbox)), denySymLinks: true);
        var deniedBytes = await File.ReadAllBytesAsync(ConfigPath(sandbox));

        var second = await InstallAsync(sandbox);
        AssertSucceeded(second);
        AssertCommand(sandbox, Command(await ConfigAsync(sandbox)), denySymLinks: false);
        Assert.NotEqual(deniedBytes, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
    }

    // --------------------------------------------------------------------- what-if ---------------

    [Fact]
    public async Task WhatIfOnAFreshWorkspaceCreatesNothing()
    {
        using var sandbox = new Sandbox();
        var result = await InstallAsync(sandbox, "-WhatIf");
        AssertSucceeded(result);

        Assert.Empty(EntryNames(sandbox));
        Assert.Equal("skipped", AgentsStatus(result));
        AssertNoBackupReported(result);
        Assert.Empty(Backups(sandbox));
        Assert.Empty(TempFiles(sandbox));
    }

    [Fact]
    public async Task WhatIfOnAnExistingConfigKeepsEveryByteAndCreatesNoBackup()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"model\":\"m\",\"mcp\":{\"filesystem-mcp\":{\"type\":\"remote\",\"command\":[\"stale\"]}}}");
        sandbox.Write(AgentsName, "# Мои правила\r\n");
        var agentsBytes = await File.ReadAllBytesAsync(AgentsPath(sandbox));

        var result = await InstallAsync(sandbox, "-WhatIf");
        AssertSucceeded(result);
        Assert.Equal(original, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Equal(agentsBytes, await File.ReadAllBytesAsync(AgentsPath(sandbox)));
        Assert.Equal(new[] { AgentsName, ConfigName }, EntryNames(sandbox));
        AssertNoBackupReported(result);
    }

    // ------------------------------------------------------------------- AGENTS.md ---------------

    [Fact]
    public async Task AgentsSectionIsNotDuplicatedOnRerun()
    {
        using var sandbox = new Sandbox();
        AssertSucceeded(await InstallAsync(sandbox));
        var created = Normalize(await File.ReadAllTextAsync(AgentsPath(sandbox)));
        Assert.Equal(1, CountOccurrences(created, SampleMarker));

        var second = await InstallAsync(sandbox);
        AssertSucceeded(second);
        var rerun = Normalize(await File.ReadAllTextAsync(AgentsPath(sandbox)));
        Assert.Equal(created, rerun);
        Assert.Equal(1, CountOccurrences(rerun, SampleMarker));
        Assert.Equal("unchanged", AgentsStatus(second));
    }

    [Fact]
    public async Task ExistingUserRulesArePreservedAndTheSectionIsAppendedOnce()
    {
        using var sandbox = new Sandbox();
        sandbox.Write(AgentsName, "# Мои правила\n\n- всегда пиши тесты\n- не трогай прод\n");
        var before = Normalize(await File.ReadAllTextAsync(AgentsPath(sandbox)));

        var result = await InstallAsync(sandbox);
        AssertSucceeded(result);
        Assert.Equal("appended", AgentsStatus(result));

        var after = Normalize(await File.ReadAllTextAsync(AgentsPath(sandbox)));
        Assert.StartsWith(before.TrimEnd(), after, StringComparison.Ordinal);
        foreach (var rule in new[] { "# Мои правила", "- всегда пиши тесты", "- не трогай прод" })
        {
            Assert.Contains(rule, after, StringComparison.Ordinal);
        }
        Assert.Equal(1, CountOccurrences(after, AgentsSectionHeader));
        Assert.Equal(1, CountOccurrences(after, SampleMarker));

        // The section marker is what a rerun must recognise, so this is the second half of the
        // idempotency contract: an appended file is not appended to a second time.
        var second = await InstallAsync(sandbox);
        AssertSucceeded(second);
        Assert.Equal("unchanged", AgentsStatus(second));
        var rerun = Normalize(await File.ReadAllTextAsync(AgentsPath(sandbox)));
        Assert.Equal(after, rerun);
        Assert.Equal(1, CountOccurrences(rerun, AgentsSectionHeader));
    }

    [Fact]
    public async Task SecondRunWithAnExistingEntryDoesNotDuplicateTheCommandPrefix()
    {
        using var sandbox = new Sandbox();
        // Positional leftovers (an old binary and an old workspace path) must not survive, because
        // they would reach the server as extra positional arguments; named options do survive.
        sandbox.Write(ConfigName, """
            {"mcp":{"filesystem-mcp":{"type":"remote","command":["old-binary","/old/path","--logDirectory=C:/logs","--maxFileBytes=1024"],"enabled":false}}}
            """);

        AssertSucceeded(await InstallAsync(sandbox));
        var first = Command(await ConfigAsync(sandbox));
        Assert.Equal(4, first.Length);
        AssertCommandPrefix(sandbox, first);
        Assert.Equal("--logDirectory=C:/logs", first[2], ignoreCase: true);
        Assert.Equal("--maxFileBytes=1024", first[3], ignoreCase: true);
        var firstBytes = await File.ReadAllBytesAsync(ConfigPath(sandbox));

        var second = await InstallAsync(sandbox);
        AssertSucceeded(second);
        Assert.Equal(first, Command(await ConfigAsync(sandbox)));
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        AssertNoBackupReported(second);
    }

    // ------------------------------------------------------------ Unicode fixture paths -----------

    [Fact]
    public async Task BinaryPathWithSpacesAndCyrillicIsWrittenAsASingleJsonArgument()
    {
        using var sandbox = new Sandbox();
        var binary = sandbox.CopyServer("Бинарник с пробелами");
        var result = await InstallWithBinaryAsync(sandbox, binary);
        AssertSucceeded(result);

        var command = Command(await ConfigAsync(sandbox));
        Assert.Equal(Slash(binary), command[0], ignoreCase: true);
        Assert.Equal(Slash(sandbox.Workspace), command[1], ignoreCase: true);
    }

    // ------------------------------------------------------------- PowerShell 7 lane --------------

    [PwshFact]
    public async Task PwshFreshInstallUsesTheUnicodeWorkspaceAsSingleArgument()
    {
        using var sandbox = new Sandbox();
        var result = await PwshAsync(sandbox);
        AssertSucceeded(result);
        var config = await ConfigAsync(sandbox);
        Assert.Equal("local", config.GetProperty("mcp").GetProperty("filesystem-mcp").GetProperty("type").GetString());
        Assert.Equal(SchemaUrl, config.GetProperty("$schema").GetString());
        AssertCommand(sandbox, Command(config), denySymLinks: false);
        Assert.Equal("created", AgentsStatus(result));
    }

    [PwshFact]
    public async Task PwshPreservesUnknownSettingsAndExistingServers()
    {
        using var sandbox = new Sandbox();
        sandbox.Write(ConfigName, """
            {"model":"existing-model","custom":{"flag":true},"mcp":{"existing-server":{"type":"local","command":["old"],"enabled":false},"filesystem-mcp":{"type":"remote","command":["stale"],"environment":{"MCP_MODE":"strict"}}}}
            """);
        var result = await PwshAsync(sandbox);
        AssertSucceeded(result);

        var config = await ConfigAsync(sandbox);
        var server = config.GetProperty("mcp").GetProperty("filesystem-mcp");
        Assert.Equal("existing-model", config.GetProperty("model").GetString());
        Assert.True(config.GetProperty("custom").GetProperty("flag").GetBoolean());
        Assert.Equal("old", config.GetProperty("mcp").GetProperty("existing-server").GetProperty("command")[0].GetString());
        Assert.Equal("strict", server.GetProperty("environment").GetProperty("MCP_MODE").GetString());
        AssertCommand(sandbox, Command(config), denySymLinks: false);
    }

    [PwshFact]
    public async Task PwshRefusesInvalidJsonWithoutTouchingTheFile()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{ invalid existing config");
        var result = await PwshAsync(sandbox);
        await AssertRefusedAsync(sandbox, result, original);
    }

    [PwshFact]
    public async Task PwshSecondRunIsByteIdempotentAndCreatesNoBackup()
    {
        using var sandbox = new Sandbox();
        AssertSucceeded(await PwshAsync(sandbox));
        var configBytes = await File.ReadAllBytesAsync(ConfigPath(sandbox));

        var second = await PwshAsync(sandbox);
        AssertSucceeded(second);
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Equal("unchanged", AgentsStatus(second));
        AssertNoBackupReported(second);
        Assert.Empty(Backups(sandbox));
    }

    [PwshFact]
    public async Task PwshDefaultAndExplicitTrueRemoveAStaleSymLinkOption()
    {
        using var sandbox = new Sandbox();
        AssertSucceeded(await PwshAsync(sandbox, DenySymLinks));
        AssertCommand(sandbox, Command(await ConfigAsync(sandbox)), denySymLinks: true);

        AssertSucceeded(await PwshAsync(sandbox, AllowSymLinksTrue));
        AssertCommand(sandbox, Command(await ConfigAsync(sandbox)), denySymLinks: false);
    }

    [PwshFact]
    public async Task PwshWhatIfWritesNothing()
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"mcp\":{\"filesystem-mcp\":{\"type\":\"remote\",\"command\":[\"stale\"]}}}");
        var result = await PwshAsync(sandbox, "-WhatIf");
        AssertSucceeded(result);
        Assert.Equal(original, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Equal(new[] { ConfigName }, EntryNames(sandbox));
        AssertNoBackupReported(result);
    }

    [PwshFact]
    public async Task PwshAsJsonEmitsOneStrictJsonLine()
    {
        using var sandbox = new Sandbox();
        var result = await PwshAsync(sandbox, "-AsJson");
        AssertSucceeded(result);
        var summary = result.Stdout.Trim();
        Assert.False(summary.Contains('\n'), "the JSON summary is not a single line: " + summary);
        Assert.Equal(JsonValueKind.Object, ServerProcess.JsonDocumentParse(summary).ValueKind);
        Assert.True(File.Exists(ConfigPath(sandbox)), "-AsJson suppressed the install.");
    }

    // ---------------------------------------------------------------- install ------------------

    private static Task<ProcessResult> InstallAsync(Sandbox sandbox, params string[] options) =>
        InstallAsync(ProcessRunner.PowerShellExecutable, sandbox, options);

    /// <summary>
    /// Runs the installer with an explicitly chosen PowerShell, so the same contract can be
    /// exercised on Windows PowerShell 5.1 and on PowerShell 7.
    /// </summary>
    private static Task<ProcessResult> InstallAsync(string powerShell, Sandbox sandbox, params string[] options) =>
        ProcessRunner.PowerShellAsync(powerShell, new[]
        {
            "-File", ScriptPath,
            "-BinaryPath", ServerProcess.DefaultExecutable,
            "-WorkspacePath", sandbox.Workspace
        }.Concat(options));

    private static Task<ProcessResult> InstallWithBinaryAsync(Sandbox sandbox, string binary, params string[] options) =>
        ProcessRunner.PowerShellAsync(new[]
        {
            "-File", ScriptPath, "-BinaryPath", binary, "-WorkspacePath", sandbox.Workspace
        }.Concat(options));

    // ------------------------------------------------------------------ state -------------------

    private static string ConfigPath(Sandbox sandbox) => Path.Combine(sandbox.Workspace, ConfigName);
    private static string AgentsPath(Sandbox sandbox) => Path.Combine(sandbox.Workspace, AgentsName);

    private static async Task<byte[]> WriteConfigAsync(Sandbox sandbox, string content) =>
        await File.ReadAllBytesAsync(sandbox.Write(ConfigName, content));

    private static async Task<JsonElement> ConfigAsync(Sandbox sandbox) =>
        ServerProcess.JsonDocumentParse(await File.ReadAllTextAsync(ConfigPath(sandbox)));

    private static string[] Command(JsonElement config) =>
        config.GetProperty("mcp").GetProperty("filesystem-mcp").GetProperty("command")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();

    /// <summary>Backups and atomic-write temp files are recognised by name, never by timestamp.</summary>
    private static string[] Matching(Sandbox sandbox, string prefix, string suffix) =>
        Directory.EnumerateFiles(sandbox.Workspace)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)
                && Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();

    private static string[] Backups(Sandbox sandbox) => Matching(sandbox, BackupPattern, ".bak");
    private static string[] TempFiles(Sandbox sandbox) => Matching(sandbox, TempPattern, ".tmp");

    private static string[] EntryNames(Sandbox sandbox) =>
        Directory.EnumerateFileSystemEntries(sandbox.Workspace).Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal).Select(name => name!).ToArray();

    // ----------------------------------------------------------------- report -------------------

    /// <summary>Value of one human report line, for example <c>Backup: &lt;path&gt;</c>.</summary>
    private static string ReportValue(ProcessResult result, string key)
    {
        var line = result.Stdout.Split('\n').Select(text => text.TrimEnd('\r'))
            .FirstOrDefault(text => text.TrimStart().StartsWith(key, StringComparison.Ordinal));
        Assert.True(line is not null, $"stdout has no '{key}' line.\nstdout: {result.Stdout}\nstderr: {result.Stderr}");
        var value = line!.TrimStart()[key.Length..].Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    /// <summary>The AGENTS.md outcome word, one of the four the contract fixes.</summary>
    private static string AgentsStatus(ProcessResult result)
    {
        var word = ReportValue(result, "AGENTS.md:").Split(' ', '(', '\t')[0];
        Assert.Contains(word, new[] { "created", "appended", "unchanged", "skipped" });
        return word;
    }

    /// <summary>A run without a backup must still print the line and must not name a real file.</summary>
    private static void AssertNoBackupReported(ProcessResult result)
    {
        var value = ReportValue(result, "Backup:");
        Assert.False(string.IsNullOrWhiteSpace(value), "The Backup line is empty: " + result.Stdout);
        Assert.False(File.Exists(value), $"This run reported a real backup file, but it preserved nothing: {value}");
        Assert.False(Path.IsPathRooted(value), $"A run without a backup reported a path: {value}");
    }

    private static void AssertSucceeded(ProcessResult result) =>
        Assert.True(result.ExitCode == 0, $"Installer exited {result.ExitCode}.\nstdout: {result.Stdout}\nstderr: {result.Stderr}");

    /// <summary>Refusal shape: non-zero exit, exact old bytes, no backup, no temp file, no AGENTS.md.</summary>
    private static async Task AssertRefusedAsync(Sandbox sandbox, ProcessResult result, byte[] original)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Empty(Backups(sandbox));
        Assert.Empty(TempFiles(sandbox));
        Assert.False(File.Exists(AgentsPath(sandbox)), "A refused run published AGENTS.md: " + result.Stdout);
    }

    private static void AssertCommand(Sandbox sandbox, string[] actual, bool denySymLinks)
    {
        var expected = new List<string> { Slash(ServerProcess.DefaultExecutable), Slash(sandbox.Workspace) };
        if (denySymLinks)
        {
            expected.Add(SymLinkOption);
        }
        Assert.Equal(expected.Count, actual.Length);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index], actual[index], ignoreCase: true);
        }
    }

    /// <summary>The two arguments the installer owns, wherever the rest of the command came from.</summary>
    private static void AssertCommandPrefix(Sandbox sandbox, string[] actual)
    {
        Assert.True(actual.Length >= 2, "the command lost the binary or the workspace: " + string.Join(", ", actual));
        Assert.Equal(Slash(ServerProcess.DefaultExecutable), actual[0], ignoreCase: true);
        Assert.Equal(Slash(sandbox.Workspace), actual[1], ignoreCase: true);
        Assert.DoesNotContain(SymLinkOption, actual);
    }

    private static string Slash(string path) => path.Replace('\\', '/');

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>The JSON line a run prints when it refuses before it has a full report.</summary>
    private static JsonElement Summary(ProcessResult result)
    {
        var line = result.Stdout.Trim().Split('\n').LastOrDefault(text => text.TrimStart().StartsWith('{'));
        Assert.True(line is not null, $"stdout has no JSON line.\nstdout: {result.Stdout}\nstderr: {result.Stderr}");
        return ServerProcess.JsonDocumentParse(line!.Trim());
    }

    /// <summary>An unknown top-level setting nested <paramref name="depth"/> objects deep.</summary>
    private static string NestedConfig(int depth) =>
        "{\"model\":\"m\",\"deep\":" + string.Concat(Enumerable.Repeat("{\"a\":", depth))
        + "{\"leaf\":\"kept\"}" + new string('}', depth) + "}";

    private static async Task SecondCommitFailureAsync(string powerShell)
    {
        using var sandbox = new Sandbox();
        var original = await WriteConfigAsync(sandbox, "{\"model\":\"m\",\"mcp\":{\"filesystem-mcp\":{\"type\":\"remote\",\"command\":[\"stale\"]}}}");
        sandbox.Write(AgentsName, "# Мои правила\r\n");
        var agentsBytes = await File.ReadAllBytesAsync(AgentsPath(sandbox));

        ProcessResult result;
        using (new FileStream(AgentsPath(sandbox), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await InstallAsync(powerShell, sandbox);
        }

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(ConfigPath(sandbox)));
        Assert.Equal(agentsBytes, await File.ReadAllBytesAsync(AgentsPath(sandbox)));
        Assert.Single(Backups(sandbox));
        Assert.Equal(original, await File.ReadAllBytesAsync(Backups(sandbox)[0]));
        Assert.Empty(TempFiles(sandbox));
        // The outcome vocabulary is not fixed by the contract; every accepted word names the restore.
        Assert.Matches("(?i)(restor|rollback|roll-back|rolled back|revert)", result.Stdout + "\n" + result.Stderr);
    }

    private static Task<ProcessResult> PwshAsync(Sandbox sandbox, params string[] options) =>
        InstallAsync(PwshFactAttribute.Executable!, sandbox, options);
}
