[CmdletBinding()]
param(
    [switch]$TestOnly,
    [int]$ExitTimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'process-safety.ps1')

function Resolve-NetBootPreviewTarget {
    param([Parameter(Mandatory)][string]$RepositoryRoot)
    $sourceDirectory = Join-Path $RepositoryRoot 'src\NetBootDhcpTool.App\bin\Release\net10.0-windows'
    $releaseDirectory = Join-Path $RepositoryRoot 'release\NetBootDhcpTool'
    $sourceExe = Join-Path $sourceDirectory 'NetBootDhcpTool.exe'
    $releaseExe = Join-Path $releaseDirectory 'NetBootDhcpTool.exe'
    if (Test-Path -LiteralPath $sourceExe) { $exe = $sourceExe }
    elseif (Test-Path -LiteralPath $releaseExe) { $exe = $releaseExe }
    else { throw 'App not found. Build the Release project or run build\publish.ps1 first.' }
    return [pscustomobject]@{
        Executable = $exe
        OwnedEntryPoints = @(
            $sourceExe,
            (Join-Path $sourceDirectory 'NetBootDhcpTool.dll'),
            $releaseExe,
            (Join-Path $releaseDirectory 'NetBootDhcpTool.dll')
        )
    }
}

if (-not $TestOnly) {
    $root = Split-Path -Parent $PSScriptRoot
    $target = Resolve-NetBootPreviewTarget $root
    Write-Host "Starting local preview after verified application cleanup: $($target.Executable)"
    Invoke-NetBootAppPreviewRestart `
        -RepositoryRoot $root `
        -PreviewExecutable $target.Executable `
        -OwnedAppEntryPoints $target.OwnedEntryPoints `
        -ExitTimeoutSeconds $ExitTimeoutSeconds
}
