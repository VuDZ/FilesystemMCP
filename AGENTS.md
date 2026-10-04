# Code style for agents

C# style for this server is in [docs/code-style.md](docs/code-style.md), next to this file.

- Read that file in full once before the first creation or edit of C# code in the current task, including tests.
- Read it in full when reviewing style or discussing changes to these rules.
- Planning, research, and discussion that do not change code do not require reading the file, unless the task is about the style itself.
- If the task moves from discussion to implementation, read the file before the first code change.
- If `docs/code-style.md` changes on disk during the task, read it again in full before the next code change.
- Apply the rules to new code and to lines this task already changes. That includes the section "Applying the rules to existing code": leave the rest of a file as it is, including member order in types this task does not edit.
- If you launch a subagent that will create, edit, or review the style of C# code, its prompt must require it to read `docs/code-style.md` in full before the first edit or before style comments, and to apply those rules only to new code and to lines that subagent changes.
- Text that lives in code is English: comments, XML docs, log and diagnostic messages, exception messages, test failure text, and asserted fragments. Localized external output kept as test data stays verbatim; project documentation keeps its current language.

# Test selection and final validation

This tree is FilesystemMcp. The regression suite is `FilesystemMcp.Tests`. There is no `RoslynMcpServer.Tests` project and no structural-analysis or analyzer-lifecycle suite. The style guide mentions `RoslynMcpServer.Tests/SourceStructure`; that project is not here. Check behavior by executing `FilesystemMcp.Tests`. Do not invent a source-text structural suite.

Run the commands from the directory that contains `FilesystemMcp/` (the AITools product tree, one level above this file). A plain `dotnet build` of `FilesystemMCP.sln` compiles the server twice under different global properties and fails with CS2012 on the shared `obj`. Pass `-m:1`, `-p:UseSharedCompilation=false`, `-p:PublishAot=false`, and `-p:PublishTrimmed=false`. Acceptance builds also pass `--no-incremental`. The configuration is Debug.

```powershell
dotnet restore FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj -p:PublishAot=false -p:PublishTrimmed=false
dotnet build FilesystemMcp/FilesystemMCP.sln --no-restore -m:1 -p:UseSharedCompilation=false -p:PublishAot=false -p:PublishTrimmed=false --no-incremental
dotnet test FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj --no-build --no-restore -p:PublishAot=false -p:PublishTrimmed=false --filter "Status=Baseline"
dotnet test FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj --no-build --no-restore -p:PublishAot=false -p:PublishTrimmed=false
```

- During implementation, run the focused spec or class (`--filter Spec=FS-01`, or one test class). That is a minimum, not the acceptance gate.
- Before final acceptance or committing code or test changes, build as above and run the complete `FilesystemMcp.Tests` suite with no filter. After a fix, rerun the affected tests, then the complete suite again.
- `Status=Baseline` is the green control set. Report passed, failed, and skipped counts. Capability skips (Unix, symlinks, and a missing PowerShell 7) are expected on a Windows host. Do not turn them into failures and do not delete them.
- `Status=KnownDefect` is empty after 1.14.0. A new failure in the full suite is a regression, except the known timing case `SearchBudgetTests.SearchThatGenuinelyExceedsTheOperationTimeoutReturnsAPartialResult`: a 2000-file walk can finish inside the operation budget and the test then fails looking for `truncation_reason`. Confirm that case in isolation before treating it as a product defect. Any other failure blocks acceptance.
- A zero-test run is not a pass. Report failures, skips, timeouts, and an unavailable runner instead of claiming full validation.
- If a Roslyn MCP build or test tool can pass `-m:1`, shared compilation off, and `PublishAot=false`, use it. If it cannot express those properties, use `dotnet` directly.
- In subagent workflows, the coordinator owns the final build and the complete test run. Review does not replace that run.
