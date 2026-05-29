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

.EXAMPLE
    cd D:\MyProject
    C:\Tools\FilesystemMCP\install2opencode.ps1

.EXAMPLE
    cd C:\Tools\FilesystemMCP
    .\install2opencode.ps1 -WorkspacePath D:\MyProject
#>
param(
    [string]$BinaryPath,
    [string]$WorkspacePath
)

# Optional: set a fixed path to the built binary (leave empty to use -BinaryPath or auto-detect next to this script).
$DefaultBinaryPath = ''

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Path not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-BinaryPath {
    param(
        [string]$ScriptRoot,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($DefaultBinaryPath)) {
        return (Resolve-FullPath $DefaultBinaryPath)
    }

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return (Resolve-FullPath $ExplicitPath)
    }

    $localBinary = Join-Path $ScriptRoot 'FilesystemMCP.exe'
    if (Test-Path -LiteralPath $localBinary) {
        return (Resolve-FullPath $localBinary)
    }

    throw @"
FilesystemMCP.exe not found next to install2opencode.ps1.
Run from the publish directory or pass -BinaryPath.
Build first: dotnet publish -c Release
"@
}

function Write-OpenCodeConfig {
    param(
        [string]$TargetPath,
        [string]$Binary,
        [string]$Workspace
    )

    $binaryJson = ($Binary -replace '\\', '/')
    $workspaceJson = ($Workspace -replace '\\', '/')

    $config = [ordered]@{
        '$schema' = 'https://opencode.ai/config.json'
        mcp       = [ordered]@{
            'filesystem-mcp' = [ordered]@{
                type    = 'local'
                command = @($binaryJson, $workspaceJson)
            }
        }
    }

    $json = ($config | ConvertTo-Json -Depth 10 -Compress)
    Write-Utf8File -Path $TargetPath -Content $json
}

function Update-AgentsFile {
    param(
        [string]$TargetPath,
        [string]$SamplePath
    )

    if (-not (Test-Path -LiteralPath $SamplePath)) {
        throw "Sample not found: $SamplePath"
    }

    $sampleContent = Get-Content -LiteralPath $SamplePath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($sampleContent)) {
        throw "Sample file is empty: $SamplePath"
    }

    if (-not (Test-Path -LiteralPath $TargetPath)) {
        Write-Utf8File -Path $TargetPath -Content ($sampleContent.TrimEnd() + "`r`n")
        return 'created'
    }

    $existingContent = Get-Content -LiteralPath $TargetPath -Raw -Encoding UTF8
    $sampleMarker = '# INITIALIZATION SEQUENCE: AI Agent Behavioral Rules'
    if ($existingContent.Contains($sampleMarker)) {
        return 'unchanged'
    }

    $sectionMarker = '# FilesystemMCP Agent Rules'
    $updated = ($existingContent.TrimEnd() + "`r`n`r`n---`r`n`r`n" + $sectionMarker + "`r`n`r`n" + $sampleContent.TrimEnd() + "`r`n")
    Write-Utf8File -Path $TargetPath -Content $updated
    return 'appended'
}

$scriptRoot = $PSScriptRoot
$currentDirectory = (Get-Location).Path
$runningFromPublishDirectory = [string]::Equals(
    (Resolve-FullPath $currentDirectory),
    (Resolve-FullPath $scriptRoot),
    [System.StringComparison]::OrdinalIgnoreCase)

$resolvedBinary = Resolve-BinaryPath -ScriptRoot $scriptRoot -ExplicitPath $BinaryPath

if ($runningFromPublishDirectory) {
    if ([string]::IsNullOrWhiteSpace($WorkspacePath)) {
        throw "Running from the publish directory. Pass -WorkspacePath with the target project root."
    }

    $resolvedWorkspace = Resolve-FullPath $WorkspacePath
}
elseif ([string]::IsNullOrWhiteSpace($WorkspacePath)) {
    $resolvedWorkspace = Resolve-FullPath $currentDirectory
}
else {
    $resolvedWorkspace = Resolve-FullPath $WorkspacePath
}

$openCodeConfigPath = Join-Path $resolvedWorkspace 'opencode.json'
$agentsPath = Join-Path $resolvedWorkspace 'AGENTS.md'
$agentsSamplePath = Join-Path $scriptRoot 'AGENTS.md.sample'

Write-OpenCodeConfig -TargetPath $openCodeConfigPath -Binary $resolvedBinary -Workspace $resolvedWorkspace
$agentsStatus = Update-AgentsFile -TargetPath $agentsPath -SamplePath $agentsSamplePath

Write-Host "FilesystemMCP OpenCode install complete."
Write-Host "  Binary:    $resolvedBinary"
Write-Host "  Workspace: $resolvedWorkspace"
Write-Host "  Config:    $openCodeConfigPath"
Write-Host "  AGENTS.md: $agentsStatus ($agentsPath)"
