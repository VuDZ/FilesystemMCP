#Requires -Version 5.1
<#
.SYNOPSIS
    Registers FilesystemMCP in the user-wide OpenCode config.

.DESCRIPTION
    Stores only the absolute path of the binary. The binary may be installed
    anywhere. OpenCode passes the session directory by starting this process
    with its working directory set to that session (config cwd is "."). The
    server uses that directory as the workspace. No project path is written,
    and the folder that contains the executable is not a workspace.

    Default config file:
      %USERPROFILE%\.config\opencode\opencode.json
    When XDG_CONFIG_HOME is set, the file is $XDG_CONFIG_HOME\opencode\opencode.json.
    Pass -ConfigPath to write somewhere else. A test must pass -ConfigPath so
    the real home config is never touched.

    Requires install2opencode.ps1 in the same directory. Does not write AGENTS.md.
    An existing opencode.jsonc with no opencode.json is refused: this installer
    merges strict JSON only.

    -V2 writes mcp.servers (OpenCode v2) when the file has no mcp map yet.
    A file that already uses mcp.servers is updated in that shape without -V2.

.EXAMPLE
    .\installglobal.ps1

.EXAMPLE
    .\installglobal.ps1 -BinaryPath C:\Tools\FilesystemMCP.exe -WhatIf

.EXAMPLE
    .\installglobal.ps1 -V2
#>
[CmdletBinding()]
param(
    [string]$BinaryPath,
    [string]$ConfigPath,
    [string]$AllowSymLinks = 'true',
    [switch]$V2,
    [switch]$WhatIf,
    [switch]$AsJson
)

$installer = Join-Path $PSScriptRoot 'install2opencode.ps1'
if (-not (Test-Path -LiteralPath $installer)) {
    Write-Error 'install2opencode.ps1 was not found next to installglobal.ps1.'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $configRoot = $env:XDG_CONFIG_HOME
    if ([string]::IsNullOrWhiteSpace($configRoot)) {
        $home = $env:USERPROFILE
        if ([string]::IsNullOrWhiteSpace($home)) { $home = $env:HOME }
        if ([string]::IsNullOrWhiteSpace($home)) {
            Write-Error 'Cannot locate the home directory for the global OpenCode config. Pass -ConfigPath.'
            exit 1
        }
        $configRoot = Join-Path $home '.config'
    }
    $ConfigPath = Join-Path (Join-Path $configRoot 'opencode') 'opencode.json'
}

$arguments = @{
    ProcessDirectory = $true
    ConfigPath       = $ConfigPath
}
if ($PSBoundParameters.ContainsKey('BinaryPath')) { $arguments.BinaryPath = $BinaryPath }
if ($PSBoundParameters.ContainsKey('AllowSymLinks')) { $arguments.AllowSymLinks = $AllowSymLinks }
if ($V2) { $arguments.V2 = $true }
if ($WhatIf) { $arguments.WhatIf = $true }
if ($AsJson) { $arguments.AsJson = $true }

& $installer @arguments
if (-not $?) { exit 1 }
