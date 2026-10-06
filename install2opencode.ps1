#Requires -Version 5.1
<#
.SYNOPSIS
    Installs FilesystemMCP into an OpenCode project (opencode.json + AGENTS.md).

.DESCRIPTION
    Run from the publish directory (next to FilesystemMCP.exe) or from the target project.

    Publish output contains: FilesystemMCP.exe, install2opencode.ps1, AGENTS.md.sample

    Binary path resolution (first match wins):
      1. $DefaultBinaryPath in this script (if set)
      2. -BinaryPath parameter
      3. FilesystemMCP.exe next to this script ($PSScriptRoot)

    Workspace path resolution:
      - Run from publish directory (cwd == script dir) -> -WorkspacePath is required
      - Run from target project (cwd != script dir) -> cwd is used when -WorkspacePath is omitted

    -ProcessDirectory is the global-install mode used by installglobal.ps1. It does not store a
    project path. The command is only the binary, and cwd is ".". OpenCode resolves that cwd
    from the session directory and passes the result as this process's working directory.
    The directory that contains the binary is not a workspace.

    Existing configuration is merged, never replaced:
      - every unknown top-level setting, every other mcp server and every extra field of
        filesystem-mcp is preserved; only mcp.filesystem-mcp is written;
      - inside filesystem-mcp, type and the command prefix (binary, workspace) are the
        installer's; named options carried by the previous command (--name) are kept, while
        positional leftovers such as an old binary or workspace path are dropped, because they
        would be passed to the server and could stop it from starting;
      - "$schema" is added only when it is absent;
      - strict JSON only: JSONC, a non-object root, a non-object "mcp" or a target that is a
        directory is refused before anything is written;
      - the first mutating run keeps a byte-exact backup of the original file next to it and
        prints its path;
      - a run whose config already matches writes nothing and creates no backup;
      - both files are prepared and validated first, then committed with a temp write plus an
        atomic replace; if the second commit fails, the first file is restored from the backup.

    This is not a crash-atomic transaction over two files. After an interruption the backup
    printed by the previous run (or the reported recovery outcome) is the recovery path:
    re-running the installer converges, and the backup still holds the user's original bytes.

    The file is ASCII-only on purpose: Windows PowerShell 5.1 decodes a BOM-less script as ANSI,
    so non-ASCII source text would be corrupted before it ever runs.

.EXAMPLE
    cd D:\MyProject
    C:\Tools\FilesystemMCP\install2opencode.ps1

.EXAMPLE
    cd C:\Tools\FilesystemMCP
    .\install2opencode.ps1 -WorkspacePath D:\MyProject -AllowSymLinks:$false

.EXAMPLE
    .\install2opencode.ps1 -WorkspacePath D:\MyProject -WhatIf -AsJson
#>
[CmdletBinding()]
param(
    [string]$BinaryPath,
    [string]$WorkspacePath,
    # Global install. The config path is the file to merge, not a project root.
    # installglobal.ps1 is the entry point colleagues run; it passes these switches.
    [string]$ConfigPath,
    [switch]$ProcessDirectory,
    [switch]$V2,
    # [string], not [bool]: a value passed with a space ("-AllowSymLinks false") arrives as a
    # string and cannot bind to a bool parameter in either 5.1 or 7. The effective boolean is
    # resolved below, where $AllowSymLinksGiven distinguishes "omitted" (true) from an explicit value.
    [string]$AllowSymLinks = 'true',
    [switch]$WhatIf,
    [switch]$AsJson
)

$script:AllowSymLinksGiven = $PSBoundParameters.ContainsKey('AllowSymLinks')
$script:AsJsonRequested = [bool]$AsJson
$script:WhatIfRequested = [bool]$WhatIf
$script:ProcessDirectory = [bool]$ProcessDirectory
$script:V2Requested = [bool]$V2

# Optional: set a fixed path to the built binary (leave empty to use -BinaryPath or auto-detect next to this script).
$DefaultBinaryPath = ''

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:AllowLinks = $true

# Report paths in the console encoding the caller actually reads. Windows PowerShell 5.1 writes
# redirected output in the OEM code page, so a workspace containing non-ASCII characters would be
# reported as "?" substitutions. Only a redirected stream is redirected this way: changing a live
# console would garble its own output.
$script:stdoutRedirected = $false
try { $script:stdoutRedirected = [System.Console]::IsOutputRedirected } catch { $script:stdoutRedirected = $false }
if ($script:stdoutRedirected) {
    try {
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [System.Console]::OutputEncoding = $utf8
    }
    catch {
        # A console that refuses the change still installs; only the report's encoding is affected.
    }
}

$ConfigName = 'opencode.json'
$AgentsName = 'AGENTS.md'
$SampleName = 'AGENTS.md.sample'
$AgentSampleMarker = '# INITIALIZATION SEQUENCE: AI Agent Behavioral Rules'
$AgentSectionMarker = '# FilesystemMCP Agent Rules'
$ServerName = 'filesystem-mcp'
$SchemaUrl = 'https://opencode.ai/config.json'
$SymLinkOption = '--allowSymLinks=false'

function Stop-Installer {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw [System.InvalidOperationException]::new($Message)
}

# Refusals before the config paths are known (options, binary, workspace) still owe a machine
# consumer the JSON line it asked for; the later refusals go through Write-RefusalAndExit, which
# knows those paths. Both keep the exit code non-zero.
function Write-EarlyRefusal {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][string]$Stage
    )

    if ($script:AsJsonRequested) {
        $report = [ordered]@{
            configAction  = 'refused'
            agentsAction  = 'not-attempted'
            backup        = ''
            backupCount   = 0
            recovery      = 'nothing-published'
            whatIf        = [bool]$script:WhatIfRequested
            allowSymLinks = $script:AllowLinks
            failed        = $Stage
            message       = $Message
        }
        $jsonArguments = @{ InputObject = $report; Depth = 5; Compress = $true }
        if ($PSVersionTable.PSVersion.Major -ge 7) { $jsonArguments['AsArray'] = $false }
        Write-Output (ConvertTo-Json @jsonArguments)
    }
    Stop-Installer $Message
}

function Resolve-AllowSymLinks {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    # A leading "$" is accepted because PowerShell users write -AllowSymLinks:$false by reflex, and
    # Windows PowerShell 5.1 passes that as the literal string "$false" for a [string] parameter.
    $normalized = $Value.Trim().TrimStart('$').ToLowerInvariant()
    switch ($normalized) {
        'true' { return $true }
        'false' { return $false }
        default { Stop-Installer "-AllowSymLinks accepts only true or false; received '$Value'." }
    }
}

# --- strict JSON (ConvertFrom-Json accepts comments, single quotes, trailing commas and even
# --- trailing garbage, so the installer validates the document itself and then parses it) ---

function Test-StrictJson {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $script:JsonText = $Text
    $script:JsonIndex = 0
    $script:JsonLength = $Text.Length

    function Skip-JsonWhitespace {
        while ($script:JsonIndex -lt $script:JsonLength) {
            $code = [int]$script:JsonText[$script:JsonIndex]
            if ($code -eq 0x20 -or $code -eq 0x09 -or $code -eq 0x0A -or $code -eq 0x0D) { $script:JsonIndex++ }
            else { break }
        }
    }

    function Read-JsonString {
        if ($script:JsonText[$script:JsonIndex] -ne '"') { Stop-Installer 'Expected a JSON string.' }
        $script:JsonIndex++
        while ($script:JsonIndex -lt $script:JsonLength) {
            $character = $script:JsonText[$script:JsonIndex]
            if ($character -eq '"') { $script:JsonIndex++; return }
            if ($character -eq '\') {
                $script:JsonIndex++
                if ($script:JsonIndex -ge $script:JsonLength) { Stop-Installer 'Unterminated JSON escape sequence.' }
                $escape = $script:JsonText[$script:JsonIndex]
                if ('"\/bfnrt'.IndexOf($escape) -lt 0) {
                    if ($escape -ne 'u') { Stop-Installer 'Invalid JSON escape sequence.' }
                    if ($script:JsonIndex + 4 -ge $script:JsonLength) { Stop-Installer 'Incomplete JSON unicode escape.' }
                    for ($offset = 1; $offset -le 4; $offset++) {
                        if ('0123456789abcdefABCDEF'.IndexOf($script:JsonText[$script:JsonIndex + $offset]) -lt 0) {
                            Stop-Installer 'Invalid JSON unicode escape.'
                        }
                    }
                    $script:JsonIndex += 4
                }
                $script:JsonIndex++
                continue
            }
            if ([int]$character -lt 0x20) { Stop-Installer 'Unescaped control character in a JSON string.' }
            $script:JsonIndex++
        }
        Stop-Installer 'Unterminated JSON string.'
    }

    # JSON numbers are ASCII digits only: [char]::IsDigit is Unicode-aware and would accept
    # Arabic-Indic or other decimal digits that then fail inside ConvertFrom-Json.
    function Test-JsonDigit {
        param([char]$Character)
        return ($Character -ge '0' -and $Character -le '9')
    }

    function Read-JsonNumber {
        $start = $script:JsonIndex
        if ($script:JsonText[$script:JsonIndex] -eq '-') { $script:JsonIndex++ }
        if ($script:JsonIndex -ge $script:JsonLength) { Stop-Installer 'Invalid JSON number.' }
        if ($script:JsonText[$script:JsonIndex] -eq '0') { $script:JsonIndex++ }
        elseif (Test-JsonDigit $script:JsonText[$script:JsonIndex]) {
            while ($script:JsonIndex -lt $script:JsonLength -and (Test-JsonDigit $script:JsonText[$script:JsonIndex])) { $script:JsonIndex++ }
        }
        else { Stop-Installer 'Invalid JSON number.' }
        if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq '.') {
            $script:JsonIndex++
            if ($script:JsonIndex -ge $script:JsonLength -or -not (Test-JsonDigit $script:JsonText[$script:JsonIndex])) { Stop-Installer 'Invalid JSON number.' }
            while ($script:JsonIndex -lt $script:JsonLength -and (Test-JsonDigit $script:JsonText[$script:JsonIndex])) { $script:JsonIndex++ }
        }
        if ($script:JsonIndex -lt $script:JsonLength -and ($script:JsonText[$script:JsonIndex] -eq 'e' -or $script:JsonText[$script:JsonIndex] -eq 'E')) {
            $script:JsonIndex++
            if ($script:JsonIndex -lt $script:JsonLength -and ($script:JsonText[$script:JsonIndex] -eq '+' -or $script:JsonText[$script:JsonIndex] -eq '-')) { $script:JsonIndex++ }
            if ($script:JsonIndex -ge $script:JsonLength -or -not (Test-JsonDigit $script:JsonText[$script:JsonIndex])) { Stop-Installer 'Invalid JSON number.' }
            while ($script:JsonIndex -lt $script:JsonLength -and (Test-JsonDigit $script:JsonText[$script:JsonIndex])) { $script:JsonIndex++ }
        }
        if ($script:JsonIndex -eq $start) { Stop-Installer 'Invalid JSON number.' }
    }

    function Read-JsonLiteral {
        param([Parameter(Mandatory = $true)][string]$Literal)
        if ($script:JsonIndex + $Literal.Length -gt $script:JsonLength) { Stop-Installer "Invalid JSON literal; expected '$Literal'." }
        if ($script:JsonText.Substring($script:JsonIndex, $Literal.Length) -cne $Literal) { Stop-Installer "Invalid JSON literal; expected '$Literal'." }
        $script:JsonIndex += $Literal.Length
    }

    function Read-JsonValue {
        Skip-JsonWhitespace
        if ($script:JsonIndex -ge $script:JsonLength) { Stop-Installer 'Unexpected end of JSON.' }
        $character = $script:JsonText[$script:JsonIndex]
        if ($character -eq '{') {
            $script:JsonIndex++
            Skip-JsonWhitespace
            if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq '}') { $script:JsonIndex++; return }
            while ($true) {
                Skip-JsonWhitespace
                Read-JsonString
                Skip-JsonWhitespace
                if ($script:JsonIndex -ge $script:JsonLength -or $script:JsonText[$script:JsonIndex] -ne ':') { Stop-Installer 'Expected a colon after a JSON object member name.' }
                $script:JsonIndex++
                Read-JsonValue
                Skip-JsonWhitespace
                if ($script:JsonIndex -ge $script:JsonLength) { Stop-Installer 'Unterminated JSON object.' }
                $delimiter = $script:JsonText[$script:JsonIndex]
                if ($delimiter -eq ',') { $script:JsonIndex++; continue }
                if ($delimiter -eq '}') { $script:JsonIndex++; return }
                Stop-Installer 'Expected a comma or a closing brace in a JSON object.'
            }
        }
        if ($character -eq '[') {
            $script:JsonIndex++
            Skip-JsonWhitespace
            if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq ']') { $script:JsonIndex++; return }
            while ($true) {
                Read-JsonValue
                Skip-JsonWhitespace
                if ($script:JsonIndex -ge $script:JsonLength) { Stop-Installer 'Unterminated JSON array.' }
                $delimiter = $script:JsonText[$script:JsonIndex]
                if ($delimiter -eq ',') { $script:JsonIndex++; continue }
                if ($delimiter -eq ']') { $script:JsonIndex++; return }
                Stop-Installer 'Expected a comma or a closing bracket in a JSON array.'
            }
        }
        if ($character -eq '"') { Read-JsonString; return }
        if ($character -eq 't') { Read-JsonLiteral -Literal 'true'; return }
        if ($character -eq 'f') { Read-JsonLiteral -Literal 'false'; return }
        if ($character -eq 'n') { Read-JsonLiteral -Literal 'null'; return }
        Read-JsonNumber
    }

    Read-JsonValue
    Skip-JsonWhitespace
    if ($script:JsonIndex -ne $script:JsonLength) { Stop-Installer 'Unexpected trailing content after the JSON document.' }
}

function ConvertFrom-StrictJson {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    Test-StrictJson -Text $Text
    # PowerShell cannot round-trip an object whose member names differ only in case:
    # ConvertFrom-Json refuses such text ("keys with different casing") and the PSCustomObject cast
    # inside ConvertTo-Json silently merges the keys. A document that cannot survive the trip would
    # be written with settings dropped, so it is refused while nothing has been changed. Only our
    # own member names are reserved, so a config that never mentions "mcp" is unaffected.
    Test-JsonCaseCollisions -Text $Text
    return (ConvertFrom-Json -InputObject $Text)
}

function Test-JsonCaseCollisions {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $script:JsonText = $Text
    $script:JsonIndex = 0
    $script:JsonLength = $Text.Length

    function Skip-Whitespace {
        while ($script:JsonIndex -lt $script:JsonLength) {
            $code = [int]$script:JsonText[$script:JsonIndex]
            if ($code -eq 0x20 -or $code -eq 0x09 -or $code -eq 0x0A -or $code -eq 0x0D) { $script:JsonIndex++ }
            else { break }
        }
    }

    function Read-StringValue {
        $script:JsonIndex++
        $builder = New-Object System.Text.StringBuilder
        while ($script:JsonIndex -lt $script:JsonLength) {
            $character = $script:JsonText[$script:JsonIndex]
            if ($character -eq '"') { $script:JsonIndex++; return $builder.ToString() }
            if ($character -eq '\') {
                $script:JsonIndex++
                if ($script:JsonIndex -ge $script:JsonLength) { return $builder.ToString() }
                $escape = $script:JsonText[$script:JsonIndex]
                if ($escape -eq 'u' -and $script:JsonIndex + 4 -lt $script:JsonLength) {
                    $code = [System.Convert]::ToInt32($script:JsonText.Substring($script:JsonIndex + 1, 4), 16)
                    $null = $builder.Append([char]$code)
                    $script:JsonIndex += 5
                    continue
                }
                if ($escape -eq 'n') { $null = $builder.Append([char]0x0A) }
                elseif ($escape -eq 't') { $null = $builder.Append([char]0x09) }
                elseif ($escape -eq 'r') { $null = $builder.Append([char]0x0D) }
                elseif ($escape -eq 'b') { $null = $builder.Append([char]0x08) }
                elseif ($escape -eq 'f') { $null = $builder.Append([char]0x0C) }
                else { $null = $builder.Append($escape) }
                $script:JsonIndex++
                continue
            }
            $null = $builder.Append($character)
            $script:JsonIndex++
        }
        return $builder.ToString()
    }

    function Skip-Value {
        Skip-Whitespace
        if ($script:JsonIndex -ge $script:JsonLength) { return }
        $character = $script:JsonText[$script:JsonIndex]
        if ($character -eq '{') {
            $script:JsonIndex++
            Skip-Whitespace
            if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq '}') { $script:JsonIndex++; return }
            while ($true) {
                Skip-Whitespace
                $null = Read-StringValue
                Skip-Whitespace
                if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq ':') { $script:JsonIndex++ }
                Skip-Value
                Skip-Whitespace
                if ($script:JsonIndex -ge $script:JsonLength) { return }
                $delimiter = $script:JsonText[$script:JsonIndex]
                $script:JsonIndex++
                if ($delimiter -eq '}') { return }
            }
        }
        if ($character -eq '[') {
            $script:JsonIndex++
            Skip-Whitespace
            if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq ']') { $script:JsonIndex++; return }
            while ($true) {
                Skip-Value
                Skip-Whitespace
                if ($script:JsonIndex -ge $script:JsonLength) { return }
                $delimiter = $script:JsonText[$script:JsonIndex]
                $script:JsonIndex++
                if ($delimiter -eq ']') { return }
            }
        }
        if ($character -eq '"') { $null = Read-StringValue; return }
        while ($script:JsonIndex -lt $script:JsonLength) {
            $stop = $script:JsonText[$script:JsonIndex]
            if ($stop -eq ',' -or $stop -eq '}' -or $stop -eq ']') { return }
            $script:JsonIndex++
        }
    }

    function Test-Object {
        $script:JsonIndex++
        Skip-Whitespace
        if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq '}') { $script:JsonIndex++; return }
        $names = @()
        while ($true) {
            Skip-Whitespace
            $name = Read-StringValue
            foreach ($seen in $names) {
                if ($seen.Equals($name, [System.StringComparison]::Ordinal)) {
                    Stop-Installer ("opencode.json has the member '{0}' twice. A JSON reader keeps only one of them, so the installer cannot tell which value you meant; remove the duplicate and run again." -f $name)
                }
                if ($seen.Equals($name, [System.StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Installer ("opencode.json has two members that differ only in case ('{0}' and '{1}'). PowerShell cannot read such a document back without merging them, so the installer refuses to change it. Rename one of them." -f $seen, $name)
                }
            }
            $names += $name
            Skip-Whitespace
            if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq ':') { $script:JsonIndex++ }
            # Walk the member value instead of skipping it: a duplicate is just as unreadable one
            # level down ("env": {"A": "1", "A": "2"}), and inside arrays of objects as well.
            Walk-Value
            Skip-Whitespace
            if ($script:JsonIndex -ge $script:JsonLength) { return }
            $delimiter = $script:JsonText[$script:JsonIndex]
            $script:JsonIndex++
            if ($delimiter -eq '}') { return }
        }
    }

    function Walk-Value {
        Skip-Whitespace
        if ($script:JsonIndex -ge $script:JsonLength) { return }
        $character = $script:JsonText[$script:JsonIndex]
        if ($character -eq '{') { Test-Object; return }
        if ($character -eq '[') {
            $script:JsonIndex++
            Skip-Whitespace
            if ($script:JsonIndex -lt $script:JsonLength -and $script:JsonText[$script:JsonIndex] -eq ']') { $script:JsonIndex++; return }
            while ($true) {
                Walk-Value
                Skip-Whitespace
                if ($script:JsonIndex -ge $script:JsonLength) { return }
                $delimiter = $script:JsonText[$script:JsonIndex]
                $script:JsonIndex++
                if ($delimiter -eq ']') { return }
            }
        }
        Skip-Value
    }

    Walk-Value
}

# --- small object helpers -------------------------------------------------------------------

# JSON object member names are case-sensitive, but every PowerShell lookup here is not: the
# PSObject property indexer, ordered-dictionary Contains and the -eq operator all fold case, so a
# user's "MCP" or "FileSystem-MCP" would be consumed and renamed as if it were ours. These two
# helpers are the only case-sensitive ways in, and members are read as *properties* rather than
# values because a helper returning a value turns an empty JSON array into $null and would hide
# `"mcp": []` from a non-object check.

function Get-ExactProperty {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($null -eq $Object) { return $null }
    foreach ($property in $Object.PSObject.Properties) {
        if ($property.Name -ceq $Name) { return $property }
    }
    return $null
}

function Test-HasExactMember {
    param(
        [Parameter(Mandatory = $true)][object]$Table,
        [Parameter(Mandatory = $true)][string]$Name
    )
    foreach ($key in $Table.Keys) {
        if ([string]$key -ceq $Name) { return $true }
    }
    return $false
}

# [ordered]@{} is a Hashtable and therefore compares keys case-insensitively, which both hides an
# existing "MCP" from a case-sensitive check and lets a later assignment overwrite it. A JSON object
# is ordered and case-sensitive, so the merged document is built in this instead - and returned as
# this, never as a [pscustomobject]: casting it back collapses case-different keys, so a user's
# "$Schema" and the installer's "$schema" would merge into one key with the wrong value.
function New-JsonObject {
    return (New-Object System.Collections.Specialized.OrderedDictionary ([System.StringComparer]::Ordinal))
}

function Test-IsJsonObject {
    param([AllowNull()][object]$Value)
    return ($null -ne $Value) -and ($Value -is [System.Management.Automation.PSCustomObject])
}

# --- paths ----------------------------------------------------------------------------------

function Resolve-ExistingPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { Stop-Installer 'An empty path was given.' }
    if (-not (Test-Path -LiteralPath $Path)) { Stop-Installer "Path not found: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Test-IsDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    # -PathType Container is total: GetAttributes would throw for a path that does not exist.
    return (Test-Path -LiteralPath $Path -PathType Container)
}

function Resolve-BinaryPath {
    param(
        [string]$ScriptRoot,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($DefaultBinaryPath)) {
        return (Resolve-ExistingPath $DefaultBinaryPath)
    }

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return (Resolve-ExistingPath $ExplicitPath)
    }

    $localBinary = Join-Path $ScriptRoot 'FilesystemMCP.exe'
    if (Test-Path -LiteralPath $localBinary) {
        return (Resolve-ExistingPath $localBinary)
    }

    Stop-Installer @"
FilesystemMCP.exe not found next to install2opencode.ps1.
Run from the publish directory or pass -BinaryPath.
Build first: dotnet publish -c Release
"@
}

function ConvertTo-SlashPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return ($Path -replace '\\', '/')
}

# --- config merge ---------------------------------------------------------------------------

function New-FilesystemServer {
    param(
        [Parameter(Mandatory = $true)][string]$Binary,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Workspace,
        [Parameter(Mandatory = $true)][bool]$AllowLinks,
        [switch]$ProcessDirectory
    )

    # A project install pins the workspace as argument 2. A global install stores only the
    # binary: OpenCode passes the session directory as the child process working directory,
    # and a stored project path would ignore that session. cwd "." is how OpenCode is told
    # to resolve the session directory; it is not the folder that contains the binary.
    if ($ProcessDirectory) { $command = @((ConvertTo-SlashPath $Binary)) }
    else { $command = @((ConvertTo-SlashPath $Binary), (ConvertTo-SlashPath $Workspace)) }
    if (-not $AllowLinks) { $command += $SymLinkOption }

    $server = New-JsonObject
    $server['type'] = 'local'
    $server['command'] = $command
    if ($ProcessDirectory) { $server['cwd'] = '.' }
    return $server
}

function Merge-FilesystemServer {
    param(
        [AllowNull()][object]$Existing,
        [Parameter(Mandatory = $true)][object]$Desired,
        [Parameter(Mandatory = $true)][bool]$AllowLinks,
        [switch]$ProcessDirectory
    )

    $existingArguments = @()
    $existingKeys = @()
    $hasExactType = $false
    $hasExactCommand = $false
    $fields = New-JsonObject
    # The user's fields keep their order. A member named exactly "type" or "command" is ours to
    # update, in place; "Type" or "Command" differ in case, collide with nothing we write alone and
    # stay as the user's own settings, with our members added when they cause no collision.
    if (Test-IsJsonObject $Existing) {
        foreach ($property in $Existing.PSObject.Properties) {
            $existingKeys += $property.Name
            if ($property.Name -ceq 'type') { $hasExactType = $true; continue }
            if ($ProcessDirectory -and $property.Name -ceq 'cwd') { continue }
            if ($ProcessDirectory -and $property.Name.Equals('cwd', [System.StringComparison]::OrdinalIgnoreCase)) {
                Stop-Installer ("opencode.json has a member '{0}' that differs only in case from 'cwd'. PowerShell cannot read such a document back without merging them, so the installer refuses to change it. Rename it." -f $property.Name)
            }
            if ($property.Name -ceq 'command') {
                $hasExactCommand = $true
                if ($null -ne $property.Value) { $existingArguments = @($property.Value) }
                continue
            }
            $fields[$property.Name] = $property.Value
        }
    }

    $binary = [string]$Desired.command[0]
    $workspace = ''
    if (-not $ProcessDirectory) { $workspace = [string]$Desired.command[1] }
    # The installer owns the command prefix: the first two arguments are always this binary and
    # this workspace, so a re-run converges instead of appending a second copy of them. Only named
    # options from the previous command are carried over, because a leftover positional argument
    # (an old binary or workspace path) becomes an extra positional argument at startup and can
    # make the server refuse to start. The one named option that is never carried over is the
    # strict-mode flag: a default install (allowSymLinks true) must remove a stale one.
    $kept = @()
    foreach ($argument in $existingArguments) {
        if ($argument -isnot [string]) { continue }
        # Any previous strict-mode spelling is dropped, not only the exact one this installer
        # writes: keeping a second --allowSymLinks argument would make the server refuse to start
        # with "duplicate startup option".
        if ($argument.StartsWith('--allowSymLinks=', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        # Only a complete option carries over. A bare "--name" whose value was a separate argument
        # is dropped along with that value: keeping the flag alone would hand the server an option
        # without its value, which the server rejects at startup, and the original pair was already
        # rejected in exactly the same way.
        if ($argument.StartsWith('--', [System.StringComparison]::Ordinal) -and
            $argument.Contains('=')) { $kept += $argument }
    }

    if ($ProcessDirectory) { $command = @($binary) + $kept }
    else { $command = @($binary, $workspace) + $kept }
    if (-not $AllowLinks) { $command += $SymLinkOption }
    # @() keeps the result an array even for a single argument: a bare string would serialize as a
    # JSON string instead of a one-element array.
    $server = New-JsonObject
    # Ours are written back on the position they already had, and only appended when they are new.
    $writtenType = $false
    $writtenCommand = $false
    $writtenCwd = $false
    foreach ($key in $existingKeys) {
        if ($key -ceq 'type') { $server['type'] = $Desired.type; $writtenType = $true; continue }
        if ($key -ceq 'command') { $server['command'] = @($command); $writtenCommand = $true; continue }
        if ($ProcessDirectory -and $key -ceq 'cwd') { $server['cwd'] = '.'; $writtenCwd = $true; continue }
        $server[$key] = $fields[$key]
    }
    if (-not $writtenCommand) { $server['command'] = @($command) }
    if (-not $writtenType) { $server['type'] = $Desired.type }
    if ($ProcessDirectory -and -not $writtenCwd) { $server['cwd'] = '.' }
    return $server
}

function Test-IsOpenCodeV2Mcp {
    param([AllowNull()][object]$Mcp)

    # v2 nests servers under mcp.servers. A v1 server that is itself named "servers"
    # carries type or command, and must not be treated as that container.
    $servers = Get-ExactProperty -Object $Mcp -Name 'servers'
    if ($null -eq $servers -or $null -eq $servers.Value -or -not (Test-IsJsonObject $servers.Value)) { return $false }
    if ($null -ne (Get-ExactProperty -Object $servers.Value -Name 'type')) { return $false }
    if ($null -ne (Get-ExactProperty -Object $servers.Value -Name 'command')) { return $false }
    return $true
}

function Merge-ServerMap {
    param(
        [AllowNull()][object]$Map,
        [Parameter(Mandatory = $true)][string]$Binary,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Workspace,
        [Parameter(Mandatory = $true)][bool]$AllowLinks,
        [switch]$ProcessDirectory
    )

    $desired = New-FilesystemServer -Binary $Binary -Workspace $Workspace -AllowLinks $AllowLinks -ProcessDirectory:$ProcessDirectory
    $merged = New-JsonObject
    $written = $false
    if ($null -ne $Map) {
        foreach ($property in $Map.PSObject.Properties) {
            if ($property.Name -ceq $ServerName) {
                if ($null -ne $property.Value -and -not (Test-IsJsonObject $property.Value)) {
                    Stop-Installer ('The "{0}" server entry is not a JSON object; refusing to replace it.' -f $ServerName)
                }
                $merged[$property.Name] = Merge-FilesystemServer -Existing $property.Value -Desired $desired -AllowLinks $AllowLinks -ProcessDirectory:$ProcessDirectory
                $written = $true
                continue
            }
            $merged[$property.Name] = $property.Value
        }
    }
    if (-not $written) {
        $merged[$ServerName] = $desired
    }
    return $merged
}

function Merge-OpenCodeConfig {
    param(
        [AllowNull()][object]$Root,
        [Parameter(Mandatory = $true)][bool]$Exists,
        [Parameter(Mandatory = $true)][string]$Binary,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Workspace,
        [Parameter(Mandatory = $true)][bool]$AllowLinks,
        [switch]$ProcessDirectory,
        [switch]$V2
    )

    $result = New-JsonObject

    # A root of null or [] arrives here as $null, exactly like an absent file. Only $Exists
    # distinguishes "no config yet" from "a config whose root is not an object", and the second
    # must be refused: merging over it would discard the whole file.
    if ($Exists -and -not (Test-IsJsonObject $Root)) {
        Stop-Installer 'The existing opencode.json is not a JSON object; refusing to replace it.'
    }

    # Inspect the members themselves, not values fetched through a helper: an empty JSON array is
    # handed back by a function as $null, which would hide a non-object "mcp" from this check.
    # An explicit null is read as "unset" and treated like an absent member; every other
    # non-object value is a refusal, because overwriting it would discard the author's setting.
    $mcpMember = Get-ExactProperty -Object $Root -Name 'mcp'
    if ($null -ne $mcpMember -and $null -ne $mcpMember.Value -and -not (Test-IsJsonObject $mcpMember.Value)) {
        Stop-Installer 'The "mcp" member of the existing opencode.json is not a JSON object.'
    }

    $mcpValue = $null
    if ($null -ne $mcpMember) { $mcpValue = $mcpMember.Value }
    $detectedV2 = Test-IsOpenCodeV2Mcp -Mcp $mcpValue
    if ($ProcessDirectory -and $V2 -and $null -ne $mcpValue -and -not $detectedV2) {
        Stop-Installer 'The existing opencode.json keeps MCP servers directly under mcp. Refusing to rewrite that map as mcp.servers.'
    }

    $mcp = New-JsonObject
    # v2 stores servers under mcp.servers. A global install follows a file that already
    # uses that shape, and -V2 selects it when the file has no mcp map yet.
    if ($ProcessDirectory -and ($detectedV2 -or $V2)) {
        $serversValue = $null
        if ($detectedV2) { $serversValue = (Get-ExactProperty -Object $mcpValue -Name 'servers').Value }
        $servers = Merge-ServerMap -Map $serversValue -Binary $Binary -Workspace $Workspace -AllowLinks $AllowLinks -ProcessDirectory
        $serversWritten = $false
        if ($null -ne $mcpValue) {
            foreach ($property in $mcpValue.PSObject.Properties) {
                if ($property.Name -ceq 'servers') {
                    $mcp[$property.Name] = $servers
                    $serversWritten = $true
                    continue
                }
                $mcp[$property.Name] = $property.Value
            }
        }
        if (-not $serversWritten) { $mcp['servers'] = $servers }
    }
    else {
        $serverMember = $null
        if ($null -ne $mcpValue) {
            # Walking the sibling servers in order keeps filesystem-mcp on the position it already had
            # instead of appending it after every other server.
            foreach ($property in $mcpValue.PSObject.Properties) {
                if ($property.Name -ceq $ServerName) {
                    # The server entry is ours to write, but not to overwrite with something else: a
                    # string, number, boolean or array in that slot is the author's setting and would
                    # be discarded. Only a JSON object can be merged, and an explicit null means unset.
                    if ($null -ne $property.Value -and -not (Test-IsJsonObject $property.Value)) {
                        Stop-Installer ('The "mcp.{0}" member of the existing opencode.json is not a JSON object; refusing to replace it.' -f $ServerName)
                    }
                    $serverMember = $property
                    $mcp[$property.Name] = Merge-FilesystemServer `
                        -Existing $property.Value `
                        -Desired (New-FilesystemServer -Binary $Binary -Workspace $Workspace -AllowLinks $AllowLinks -ProcessDirectory:$ProcessDirectory) `
                        -AllowLinks $AllowLinks `
                        -ProcessDirectory:$ProcessDirectory
                    continue
                }
                $mcp[$property.Name] = $property.Value
            }
        }
        if ($null -eq $serverMember) {
            $mcp[$ServerName] = New-FilesystemServer -Binary $Binary -Workspace $Workspace -AllowLinks $AllowLinks -ProcessDirectory:$ProcessDirectory
        }
    }

    # The user's top-level members keep their order. "mcp" is rewritten at its own position; when
    # the user has no "mcp" at all, this installer adds "$schema" first and then "mcp", which is the
    # order a fresh install has always produced. "$schema" is only ever added when no member of
    # exactly that name exists: the JSON key is case-sensitive, so the user's "$Schema" is a
    # different setting and stays untouched.
    $mcpWrittenInPlace = $false
    if ($null -ne $Root) {
        foreach ($property in $Root.PSObject.Properties) {
            if ($property.Name -ceq 'mcp') {
                $result[$property.Name] = $mcp
                $mcpWrittenInPlace = $true
                continue
            }
            $result[$property.Name] = $property.Value
        }
    }
    if (-not $mcpWrittenInPlace) {
        if (-not (Test-HasExactMember -Table $result -Name '$schema')) { $result['$schema'] = $SchemaUrl }
        $result['mcp'] = $mcp
        return $result
    }
    if (-not (Test-HasExactMember -Table $result -Name '$schema')) { $result['$schema'] = $SchemaUrl }

    return $result
}

# --- JSON output ----------------------------------------------------------------------------

function Copy-NonAsciiEscaping {
    param([Parameter(Mandatory = $true)][string]$Text)

    # Windows PowerShell 5.1 always escapes non-ASCII and has no EscapeHandling switch; the
    # only way to keep the second run byte-identical is to mirror what the file already uses.
    if ($PSVersionTable.PSVersion.Major -lt 7) { return $Text.Contains('\u') }

    $inString = $false
    $index = 0
    while ($index -lt $Text.Length) {
        $character = $Text[$index]
        if ($inString) {
            if ($character -eq '\') {
                if ($index + 1 -lt $Text.Length -and $Text[$index + 1] -eq 'u') { return $true }
                $index += 2
                continue
            }
            if ($character -eq '"') { $inString = $false }
        }
        elseif ($character -eq '"') { $inString = $true }
        $index++
    }
    return $false
}

function ConvertTo-ConfigJson {
    param([Parameter(Mandatory = $true)][object]$Value)

    # 100 is the highest Depth Windows PowerShell 5.1 accepts, so it is the effective limit for the
    # document. Exceeding it does not throw: ConvertTo-Json silently replaces everything below the
    # limit with a string, which is why the caller verifies the round trip before writing.
    $arguments = @{ InputObject = $Value; Depth = 100; Compress = $true }
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $arguments['AsArray'] = $false
        if ($script:EscapeNonAscii) { $arguments['EscapeHandling'] = 'EscapeNonAscii' }
    }
    # PowerShell 7 reports the truncation as a warning on stdout, which would be the only line a
    # -AsJson consumer sees. The caller's round-trip check reports the same condition as a refusal.
    $previousWarningPreference = $WarningPreference
    try {
        $WarningPreference = 'SilentlyContinue'
        $json = ConvertTo-Json @arguments
    }
    finally {
        $WarningPreference = $previousWarningPreference
    }
    return $json
}

function Get-JsonContainerCount {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return 0 }
    if ($Value -is [System.Management.Automation.PSCustomObject] -or $Value -is [System.Collections.IDictionary]) {
        $count = 1
        if ($Value -is [System.Collections.IDictionary]) {
            foreach ($key in $Value.Keys) { $count += (Get-JsonContainerCount -Value $Value[$key]) }
            return $count
        }
        foreach ($property in $Value.PSObject.Properties) { $count += (Get-JsonContainerCount -Value $property.Value) }
        return $count
    }
    if ($Value -is [System.Array]) {
        $count = 1
        foreach ($item in $Value) { $count += (Get-JsonContainerCount -Value $item) }
        return $count
    }
    return 0
}

function Test-JsonRoundTrip {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )

    # A truncated serialization collapses nested values into strings, which removes containers.
    # Counting them before and after is the only reliable witness: neither shell reports the
    # truncation as an error, and the result would still be valid JSON.
    $expected = Get-JsonContainerCount -Value $Value
    $actual = Get-JsonContainerCount -Value (ConvertFrom-StrictJson -Text $Text)
    if ($expected -ne $actual) {
        Stop-Installer ("The merged configuration is nested too deeply to serialize without losing settings ({0} nested objects/arrays, {1} after serialization). Refusing to write a config that would drop them; simplify the nesting of the unknown settings." -f $expected, $actual)
    }

    # A non-finite number is a value the serializer cannot reproduce as a number: PowerShell 7
    # quotes it into a string ("Infinity"), which silently changes the type of the setting.
    Assert-FiniteNumbers -Value $Value
}

function Assert-FiniteNumbers {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return }
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) { Assert-FiniteNumbers -Value $Value[$key] }
        return
    }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) { Assert-FiniteNumbers -Value $property.Value }
        return
    }
    if ($Value -is [System.Array]) {
        foreach ($item in $Value) { Assert-FiniteNumbers -Value $item }
        return
    }
    if ($Value -is [double] -and ([double]::IsNaN($Value) -or [double]::IsInfinity($Value))) {
        Stop-Installer ("A number in the merged configuration is not finite ({0}) and cannot be written back as a number. Refusing to change it into a string; fix that value in {1}." -f $Value, $ConfigName)
    }
}

# --- file IO --------------------------------------------------------------------------------

function Read-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$StrictUtf8
    )
    # Byte-level read: Get-Content -Raw would decode a BOM-less file as ANSI on Windows PowerShell 5.1.
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $offset = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $offset = 3 }
    # JSON text is UTF-8 by definition, so a config that is not valid UTF-8 is not a document this
    # installer may rewrite: the lenient decoder would silently replace the bytes with U+FFFD and
    # report success. The AGENTS.md read stays lenient, because that file is not a JSON document.
    $encoding = if ($StrictUtf8) { New-Object System.Text.UTF8Encoding $false, $true }
    else { New-Object System.Text.UTF8Encoding $false, $false }
    return $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
}

function ConvertTo-Utf8Bytes {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    return $utf8NoBom.GetBytes($Text)
}

function Write-BytesAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        # Runs once the new content is safely on disk in the temp file and before the rename, so a
        # backup is only ever taken for a write that can still happen. The hook returns the value
        # the caller wants back (the installer uses it to report the backup path).
        [scriptblock]$PreCommit
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    $tempPath = Join-Path $directory ([System.IO.Path]::GetFileName($Path) + '.filesystemmcp-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $result = $null
    $committed = $false
    $renameFailed = $false
    try {
        [System.IO.File]::WriteAllBytes($tempPath, $Bytes)
        # The hook runs *outside* the rename's recovery path below: it prepares what the caller
        # needs before the swap (the installer takes its backup here), and a failure in it means the
        # write must not happen at all. Letting it fall into the fallback would publish a config
        # whose previous bytes were never preserved.
        if ($null -ne $PreCommit) { $result = & $PreCommit $tempPath }
        try {
            # Replacing an existing file with File.Replace is an atomic swap: the destination never
            # disappears, so a reader sees either the whole old file or the whole new one. Move-Item
            # -Force is not equivalent: it removes the destination first and renames afterwards,
            # which leaves a window where the path exists as nothing at all.
            # The third argument must be [NullString]::Value: PowerShell turns $null and '' into an
            # empty *path*, which Replace rejects, and .NET Framework (Windows PowerShell 5.1) has no
            # overload without a backup path, so this sentinel is the portable way to say "keep
            # none". A destination that does not exist yet cannot be replaced, so a new file is
            # renamed into place without an overwrite.
            if (Test-Path -LiteralPath $Path) {
                [System.IO.File]::Replace($tempPath, $Path, [NullString]::Value)
            }
            else {
                [System.IO.File]::Move($tempPath, $Path)
            }
            $committed = $true
        }
        catch {
            # Only the swap itself is retried, and only for a destination this process may write but
            # not delete: ReplaceFile needs DELETE access to the target, while Move-Item achieves the
            # same swap with write access alone (it deletes first, so it is a fallback, not the
            # normal path). The preparation above has already succeeded at this point.
            $renameFailed = $true
            if ((Test-Path -LiteralPath $tempPath) -and (Test-Path -LiteralPath $Path)) {
                try {
                    Move-Item -LiteralPath $tempPath -Destination $Path -Force
                    $committed = $true
                }
                catch {
                    # Both ways failed; the original failure is reported below.
                }
            }
            if (-not $committed) { throw }
        }
    }
    catch {
        if (-not $committed) {
            # The commit never happened, so a backup the hook already took describes a change that
            # was not made. Removing it keeps "a backup exists" equal to "this run replaced the
            # file"; the one backup that must survive - the previous content restored after a later
            # failure - belongs to a call that returned normally.
            if ($null -ne $PreCommit -and -not [string]::IsNullOrWhiteSpace($result) -and (Test-Path -LiteralPath $result)) {
                try { Remove-Item -LiteralPath $result -Force }
                catch { Write-Warning ("Could not remove the backup {0} of a write that did not happen: {1}" -f $result, $_.Exception.Message) }
            }
            if (-not $renameFailed) {
                # The failure came from WriteAllBytes or from the hook, not from the swap: the caller
                # has to see it rather than a renamed file it was never told about.
                Write-Verbose ("The atomic write of {0} failed before the swap: {1}" -f $Path, $_.Exception.Message)
            }
            throw
        }
    }
    finally {
        # Cleanup must not replace the real error: if the rename failed because the target is
        # denied, removing our own temp file can fail for the same reason.
        if (Test-Path -LiteralPath $tempPath) {
            try { Remove-Item -LiteralPath $tempPath -Force }
            catch { Write-Warning ("Could not remove the temporary file {0}: {1}" -f $tempPath, $_.Exception.Message) }
        }
    }
    return $result
}

function Clear-ReadOnlyAttribute {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return }
    $attributes = [System.IO.File]::GetAttributes($Path)
    if (($attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
        [System.IO.File]::SetAttributes($Path, ($attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)))
    }
}

function New-ConfigBackup {
    param([Parameter(Mandatory = $true)][string]$Path)

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
    $backupPath = Join-Path $directory ([System.IO.Path]::GetFileName($Path) + '.filesystemmcp-backup-' + $stamp + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.bak')
    [System.IO.File]::Copy($Path, $backupPath, $false)
    # Once the copy exists it has to be either returned to the caller or removed before this
    # function leaves: a backup nobody is told about would break the rule that a .bak file means
    # "this run replaced the file", and the next run would add a second one next to it.
    try {
        Clear-ReadOnlyAttribute -Path $backupPath
    }
    catch {
        try { Remove-Item -LiteralPath $backupPath -Force }
        catch { Write-Warning ("Could not remove the incomplete backup {0}: {1}" -f $backupPath, $_.Exception.Message) }
        throw
    }
    return $backupPath
}

function Get-OwnedTempFiles {
    param([Parameter(Mandatory = $true)][string]$Path)

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    return @(Get-ChildItem -LiteralPath $directory -Filter ([System.IO.Path]::GetFileName($Path) + '.filesystemmcp-*.tmp') -Force -ErrorAction SilentlyContinue)
}

# --- AGENTS.md ------------------------------------------------------------------------------

# Removes only the final line break, never the spaces before it: an existing AGENTS.md has to be
# preserved verbatim, and TrimEnd() would also eat the user's trailing whitespace.
function Remove-TrailingLineBreak {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    return $Text -replace '(\r\n|\n|\r)+$', ''
}

function Get-AgentsPlan {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$SamplePath
    )

    if (-not (Test-Path -LiteralPath $SamplePath)) { Stop-Installer "Sample not found: $SamplePath" }
    if (Test-IsDirectory $SamplePath) { Stop-Installer "Sample path is a directory, not a file: $SamplePath" }
    $sampleContent = Remove-TrailingLineBreak (Read-TextFile -Path $SamplePath)
    if ([string]::IsNullOrWhiteSpace($sampleContent)) { Stop-Installer "Sample file is empty: $SamplePath" }

    if (-not (Test-Path -LiteralPath $TargetPath)) {
        return [pscustomobject]@{
            Status  = 'created'
            Bytes   = (ConvertTo-Utf8Bytes ($sampleContent + "`r`n"))
            Changes = $true
        }
    }

    if (Test-IsDirectory $TargetPath) { Stop-Installer "AGENTS.md path is a directory, not a file: $TargetPath" }
    $existingContent = Read-TextFile -Path $TargetPath
    # Either marker means the section is already installed. Only the sample heading is checked by
    # the old script, so a user who edits that one line gets the whole section appended a second
    # time on the next run; the section header is evidence in its own right.
    if ($existingContent.Contains($AgentSampleMarker) -or $existingContent.Contains($AgentSectionMarker)) {
        return [pscustomobject]@{ Status = 'unchanged'; Bytes = $null; Changes = $false }
    }

    $updated = (Remove-TrailingLineBreak $existingContent) + "`r`n`r`n---`r`n`r`n" + $AgentSectionMarker + "`r`n`r`n" + $sampleContent + "`r`n"
    return [pscustomobject]@{
        Status  = 'appended'
        Bytes   = (ConvertTo-Utf8Bytes $updated)
        Changes = $true
    }
}

# --- main -----------------------------------------------------------------------------------

# Refuse a bad boolean before anything else happens, so no other validation message can hide it.
if (-not $script:AllowSymLinksGiven) { $script:AllowLinks = $true }
else {
    try { $script:AllowLinks = Resolve-AllowSymLinks -Value $AllowSymLinks }
    catch { Write-EarlyRefusal -Stage 'options' -Message $_.Exception.Message }
}
if ($script:V2Requested -and -not $script:ProcessDirectory) {
    Write-EarlyRefusal -Stage 'options' -Message '-V2 is only valid together with -ProcessDirectory.'
}
if (-not $script:ProcessDirectory -and -not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    Write-EarlyRefusal -Stage 'options' -Message '-ConfigPath is only valid together with -ProcessDirectory.'
}

$scriptRoot = $PSScriptRoot
$currentDirectory = (Get-Location).Path
$runningFromPublishDirectory = [string]::Equals(
    (Resolve-ExistingPath $currentDirectory),
    (Resolve-ExistingPath $scriptRoot),
    [System.StringComparison]::OrdinalIgnoreCase)

try {
    $resolvedBinary = Resolve-BinaryPath -ScriptRoot $scriptRoot -ExplicitPath $BinaryPath
}
catch { Write-EarlyRefusal -Stage 'binary' -Message $_.Exception.Message }
if (Test-IsDirectory $resolvedBinary) {
    Write-EarlyRefusal -Stage 'binary' -Message "-BinaryPath must be a file, but it is a directory: $resolvedBinary"
}

if ($script:ProcessDirectory) {
    if (-not [string]::IsNullOrWhiteSpace($WorkspacePath)) {
        Write-EarlyRefusal -Stage 'workspace' -Message '-ProcessDirectory does not take -WorkspacePath. OpenCode passes the session directory as the process working directory; the folder that contains the binary is not a workspace.'
    }
    if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
        Write-EarlyRefusal -Stage 'config' -Message '-ProcessDirectory requires -ConfigPath.'
    }
    $resolvedWorkspace = ''
    $openCodeConfigPath = $ConfigPath
    $configDirectory = [System.IO.Path]::GetDirectoryName($openCodeConfigPath)
    if ([string]::IsNullOrWhiteSpace($configDirectory)) {
        Write-EarlyRefusal -Stage 'config' -Message "-ConfigPath must be a file path: $ConfigPath"
    }
    $jsoncSibling = Join-Path $configDirectory 'opencode.jsonc'
    if ((Test-Path -LiteralPath $jsoncSibling) -and -not (Test-Path -LiteralPath $openCodeConfigPath)) {
        Write-EarlyRefusal -Stage 'config' -Message "Refusing to create opencode.json beside an existing opencode.jsonc. This installer merges strict JSON only: $jsoncSibling"
    }
    $agentsPath = ''
    $agentsSamplePath = ''
}
else {
    try {
        if ($runningFromPublishDirectory) {
            if ([string]::IsNullOrWhiteSpace($WorkspacePath)) {
                Write-EarlyRefusal -Stage 'workspace' -Message 'Running from the publish directory. Pass -WorkspacePath with the target project root.'
            }
            $resolvedWorkspace = Resolve-ExistingPath $WorkspacePath
        }
        elseif ([string]::IsNullOrWhiteSpace($WorkspacePath)) {
            $resolvedWorkspace = Resolve-ExistingPath $currentDirectory
        }
        else {
            $resolvedWorkspace = Resolve-ExistingPath $WorkspacePath
        }
    }
    catch { Write-EarlyRefusal -Stage 'workspace' -Message $_.Exception.Message }
    if (-not (Test-IsDirectory $resolvedWorkspace)) {
        Write-EarlyRefusal -Stage 'workspace' -Message "-WorkspacePath must be a directory, but it is a file: $resolvedWorkspace"
    }

    $openCodeConfigPath = Join-Path $resolvedWorkspace $ConfigName
    $agentsPath = Join-Path $resolvedWorkspace $AgentsName
    $agentsSamplePath = Join-Path $scriptRoot $SampleName
}

if (Test-Path -LiteralPath $openCodeConfigPath -PathType Container) {
    Write-EarlyRefusal -Stage $ConfigName -Message "The opencode.json target is a directory, not a file: $openCodeConfigPath"
}
if (-not $script:ProcessDirectory -and (Test-Path -LiteralPath $agentsPath -PathType Container)) {
    Write-EarlyRefusal -Stage $AgentsName -Message "The AGENTS.md target is a directory, not a file: $agentsPath"
}

# Report a refusal that happened before any write in the same shape as a report, so a machine
# consumer that passed -AsJson always receives JSON on stdout instead of a bare host message.
function Write-RefusalAndExit {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][string]$Stage
    )

    if ($AsJson) {
        $report = [ordered]@{
            config        = $openCodeConfigPath
            agents        = $agentsPath
            configAction  = 'refused'
            agentsAction  = 'not-attempted'
            backup        = ''
            backupCount   = 0
            recovery      = 'nothing-published'
            whatIf        = [bool]$WhatIf
            allowSymLinks = $script:AllowLinks
            failed        = $Stage
            message       = $Message
        }
        $jsonArguments = @{ InputObject = $report; Depth = 5; Compress = $true }
        if ($PSVersionTable.PSVersion.Major -ge 7) { $jsonArguments['AsArray'] = $false }
        Write-Output (ConvertTo-Json @jsonArguments)
    }
    Stop-Installer $Message
}

# 1. Parse the existing config strictly; refuse without touching the file. The read is part of
# preparation, so a config that cannot be read (locked without sharing, denied, invalid bytes) is a
# clean refusal rather than a raw exception, exactly like the AGENTS.md preparation below.
$existingRoot = $null
$existingBytes = $null
$existingText = $null
$configExists = Test-Path -LiteralPath $openCodeConfigPath
if ($configExists) {
    try {
        $existingBytes = [System.IO.File]::ReadAllBytes($openCodeConfigPath)
        $existingText = Read-TextFile -Path $openCodeConfigPath -StrictUtf8
    }
    catch {
        Write-RefusalAndExit -Stage $ConfigName -Message ("The existing {0} could not be read ({1}); refusing to change it." -f $ConfigName, $_.Exception.Message)
    }
    try { $existingRoot = ConvertFrom-StrictJson -Text $existingText }
    catch {
        Write-RefusalAndExit -Stage $ConfigName -Message ("The existing {0} is not strict JSON; refusing to change it: {1}" -f $ConfigName, $_.Exception.Message)
    }
}

# 2. Build the merged document.
$script:EscapeNonAscii = $false
if ($null -ne $existingText) { $script:EscapeNonAscii = Copy-NonAsciiEscaping -Text $existingText }
try {
    $mergedRoot = Merge-OpenCodeConfig -Root $existingRoot -Exists $configExists -Binary $resolvedBinary -Workspace $resolvedWorkspace -AllowLinks $script:AllowLinks -ProcessDirectory:$script:ProcessDirectory -V2:$script:V2Requested
    # The serialization warning ("truncated as serialization has exceeded the set depth") would be
    # the only stdout line for a machine consumer, which is why the check that follows throws its
    # own message instead of relying on it.
    $desiredText = ConvertTo-ConfigJson -Value $mergedRoot
    $null = Test-StrictJson -Text $desiredText
    Test-JsonRoundTrip -Value $mergedRoot -Text $desiredText
    $desiredBytes = ConvertTo-Utf8Bytes ($desiredText + "`n")
}
catch {
    Write-RefusalAndExit -Stage 'merge' -Message ("The merged {0} could not be prepared; refusing to change it: {1}" -f $ConfigName, $_.Exception.Message)
}

$configChanges = $true
if ($null -ne $existingBytes -and $existingBytes.Length -eq $desiredBytes.Length) {
    $configChanges = $false
    for ($index = 0; $index -lt $existingBytes.Length; $index++) {
        if ($existingBytes[$index] -ne $desiredBytes[$index]) { $configChanges = $true; break }
    }
}

# 3. Prepare AGENTS.md before anything is written. A locked or unreadable AGENTS.md is a prepare
# failure, not a commit failure, so the config must not be touched for it either.
$agentsPlan = $null
$agentsPrepareFailure = $null
if ($script:ProcessDirectory) {
    # A global registration does not own a project AGENTS.md. The session directory is
    # chosen later by OpenCode, and this config file is not that directory.
    $agentsPlan = [pscustomobject]@{ Status = 'skipped'; Bytes = $null; Changes = $false }
    $agentsPrepareFailure = $null
}
else {
    try { $agentsPlan = Get-AgentsPlan -TargetPath $agentsPath -SamplePath $agentsSamplePath }
    catch { $agentsPrepareFailure = $_.Exception.Message }
}
# The commit below runs in a script block so a failed step can leave it early; PowerShell has no
# labelled break. An assignment inside a script block creates a *local* of that block, so every
# value the commit reports has to be written through the script scope and copied back afterwards.
$script:recoveryOutcome = 'none'
$script:backupPath = ''
$script:location = ''
$script:outcome = 'none'
$script:failure = $null
$script:agentsStatus = if ($null -eq $agentsPlan) { 'failed' } else { $agentsPlan.Status }
# A plan that could not be built is a failure regardless of the mode; the branches below only
# override this when there is a plan to report.
if ($null -ne $agentsPrepareFailure) { $script:agentsStatus = 'failed' }

if ($WhatIf) {
    # A dry run still has to prepare: preparation is what proves the plan can be executed, so a
    # plan that could not even be built (unreadable AGENTS.md, missing sample) is a refusal and not
    # a report that the install would succeed.
    if ($null -ne $agentsPrepareFailure) {
        $script:location = 'agents'
        $script:failure = $agentsPrepareFailure
        $script:outcome = 'config-unchanged'
    }
    else {
        $script:agentsStatus = 'skipped'
    }
}
else {
    # The generated arguments are not "test run" against the binary: starting the server would
    # write its own log files, and it exits silently on valid arguments, so a launch proves
    # nothing that the file and directory checks above do not already prove. A rejected option
    # is reported by the server to the client at first start.
    & {

        if ($null -ne $agentsPrepareFailure) {
            $script:location = 'agents'
            $script:failure = $agentsPrepareFailure
            $script:outcome = 'config-unchanged'
            return
        }

        # 4. Commit opencode.json; if that fails, nothing of ours is published.
        if ($configChanges) {
            try {
                if ($script:ProcessDirectory) {
                    $configParent = [System.IO.Path]::GetDirectoryName($openCodeConfigPath)
                    if (-not (Test-Path -LiteralPath $configParent)) {
                        New-Item -ItemType Directory -Path $configParent -Force | Out-Null
                    }
                }
                if (Test-Path -LiteralPath $openCodeConfigPath) { Clear-ReadOnlyAttribute -Path $openCodeConfigPath }
                # The backup is taken from inside the write, after the new content exists in the temp
                # file and before the rename, so a run that never publishes anything cannot leave a
                # backup behind for a file it did not change.
                $script:backupPath = Write-BytesAtomic -Path $openCodeConfigPath -Bytes $desiredBytes -PreCommit {
                    param($tempPath)
                    if (-not (Test-Path -LiteralPath $openCodeConfigPath)) { return '' }
                    return (New-ConfigBackup -Path $openCodeConfigPath)
                }
            }
            catch {
                $script:location = 'config'
                $script:outcome = 'nothing-published'
                # Preparing the plan is not installing it: nothing was written to AGENTS.md.
                $script:agentsStatus = 'not-attempted'
                $script:failure = $_.Exception.Message
                return
            }

            # 5. Commit AGENTS.md; if that fails, put the previous config back.
            if ($agentsPlan.Changes) {
                try {
                    Clear-ReadOnlyAttribute -Path $agentsPath
                    Write-BytesAtomic -Path $agentsPath -Bytes $agentsPlan.Bytes
                }
                catch {
                    $script:location = 'agents'
                    $script:agentsStatus = 'failed'
                    $script:failure = $_.Exception.Message
                    if ([string]::IsNullOrWhiteSpace($script:backupPath)) {
                        try {
                            Remove-Item -LiteralPath $openCodeConfigPath -Force
                            $script:outcome = 'removed-new-config'
                        }
                        catch {
                            $script:outcome = 'complete-failure'
                            $script:failure = $script:failure + '; and the new opencode.json could not be removed: ' + $_.Exception.Message
                        }
                    }
                    else {
                        try {
                            Write-BytesAtomic -Path $openCodeConfigPath -Bytes ([System.IO.File]::ReadAllBytes($script:backupPath))
                            $script:outcome = 'restored-from-backup'
                        }
                        catch {
                            $script:outcome = 'complete-failure'
                            $script:failure = $script:failure + '; and the previous opencode.json could not be restored from ' + $script:backupPath + ': ' + $_.Exception.Message
                        }
                    }
                    return
                }
            }
        }
        elseif ($agentsPlan.Changes) {
            try {
                Clear-ReadOnlyAttribute -Path $agentsPath
                Write-BytesAtomic -Path $agentsPath -Bytes $agentsPlan.Bytes
            }
            catch {
                $script:location = 'agents'
                $script:agentsStatus = 'failed'
                $script:failure = $_.Exception.Message
                if (Test-Path -LiteralPath $openCodeConfigPath) { $script:outcome = 'config-unchanged' }
                else { $script:outcome = 'no-previous-config' }
                return
            }
        }
    }
}

$backupPath = $script:backupPath
$recoveryOutcome = $script:recoveryOutcome
$location = $script:location
$outcome = $script:outcome
$failure = $script:failure
$agentsStatus = $script:agentsStatus

if ($null -ne $failure) {
    $recoveryOutcome = $outcome
    # A failure is never reported as a successful config update. It can be "unchanged", "updated"
    # (the config had been written before the AGENTS commit failed and was restored), or
    # "not-restored" where the recovery itself failed and the file may still hold the new content.
    $failedConfigAction = 'unchanged'
    if ($location -eq 'agents' -and $configChanges -and $outcome -eq 'restored-from-backup') { $failedConfigAction = 'updated' }
    if ($outcome -eq 'complete-failure') { $failedConfigAction = 'not-restored' }
    $report = [ordered]@{
        binary        = $resolvedBinary
        workspace     = $resolvedWorkspace
        config        = $openCodeConfigPath
        agents        = $agentsPath
        configAction  = $failedConfigAction
        agentsAction  = $agentsStatus
        backup        = $backupPath
        backupCount   = $(if ([string]::IsNullOrWhiteSpace($backupPath)) { 0 } else { 1 })
        recovery      = $outcome
        whatIf        = [bool]$script:WhatIfRequested
        allowSymLinks = $script:AllowLinks
        failed        = $location
        message       = $failure
    }
    $jsonArguments = @{ InputObject = $report; Depth = 5; Compress = $true }
    if ($PSVersionTable.PSVersion.Major -ge 7) { $jsonArguments['AsArray'] = $false }
    if ($AsJson) { Write-Output (ConvertTo-Json @jsonArguments) }
    Write-Host ("FilesystemMCP OpenCode install FAILED at the {0} stage." -f $location)
    Write-Host "  Failure:       $failure"
    Write-Host "  Recovery:      $outcome"
    Write-Host "  Backup:        $(if ([string]::IsNullOrWhiteSpace($backupPath)) { 'none' } else { $backupPath })"
    Stop-Installer ("The {0} stage failed: {1}. Recovery: {2}." -f $location, $failure, $outcome)
}

# A function returning an empty array hands back $null, so wrap it before touching .Count:
# Set-StrictMode -Version Latest rejects the property on $null.
$ownedTemp = @(Get-OwnedTempFiles -Path $openCodeConfigPath)
$backupCount = 0
if (-not [string]::IsNullOrWhiteSpace($backupPath)) { $backupCount = 1 }

if ($WhatIf) { $configAction = $(if ($configChanges) { 'would-update' } else { 'would-not-change' }) }
elseif ($configChanges) { $configAction = 'updated' }
else { $configAction = 'unchanged' }

if ($AsJson) {
    $summary = [ordered]@{
        binary        = $resolvedBinary
        workspace     = $resolvedWorkspace
        config        = $openCodeConfigPath
        agents        = $agentsPath
        configAction  = $configAction
        agentsAction  = $agentsStatus
        backup        = $backupPath
        backupCount   = $backupCount
        recovery      = $recoveryOutcome
        whatIf        = [bool]$WhatIf
        allowSymLinks = $script:AllowLinks
    }
    $jsonArguments = @{ InputObject = $summary; Depth = 5; Compress = $true }
    if ($PSVersionTable.PSVersion.Major -ge 7) { $jsonArguments['AsArray'] = $false }
    Write-Output (ConvertTo-Json @jsonArguments)
}
else {
    Write-Host 'FilesystemMCP OpenCode install complete.'
    Write-Host "  Binary:        $resolvedBinary"
    if ($script:ProcessDirectory) { $workspaceLabel = 'OpenCode session directory (passed as the process working directory, not the binary folder)' }
    else { $workspaceLabel = $resolvedWorkspace }
    Write-Host "  Workspace:     $workspaceLabel"
    Write-Host "  Config:        $openCodeConfigPath ($configAction)"
    Write-Host "  AGENTS.md:     $agentsStatus ($agentsPath)"
    Write-Host "  Backup:        $(if ([string]::IsNullOrWhiteSpace($backupPath)) { 'none (nothing to preserve)' } else { $backupPath })"
    Write-Host "  Recovery:      $recoveryOutcome"
    Write-Host "  allowSymLinks: $script:AllowLinks"
    if ($WhatIf) { Write-Host '  Mode:          what-if (nothing was written)' }
    if ($ownedTemp.Count -gt 0) {
        Write-Host "  Temp files:    $($ownedTemp.Count) leftover temp file(s) in $([System.IO.Path]::GetDirectoryName($openCodeConfigPath))"
    }
}
