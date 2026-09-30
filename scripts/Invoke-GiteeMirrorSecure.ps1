#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag = 'v1.0.20',
    [string]$AssetDirectory = 'D:\Release\v1.0.20'
)

$ErrorActionPreference = 'Stop'
$runId = [guid]::NewGuid().ToString('N')
$releaseDirectory = 'D:\Release'
$statusPath = Join-Path $releaseDirectory "_t19-gitee-mirror-$runId.status.json"
$telemetryPath = Join-Path $releaseDirectory "_t19-gitee-mirror-$runId.telemetry.json"
$publisherPath = Join-Path $PSScriptRoot 'Publish-GiteeMirror.ps1'
$secureToken = $null
$tokenText = $null
$tokenBstr = [IntPtr]::Zero
$state = 'WAITING_FOR_TOKEN'

function Write-SecureMirrorStatus([string]$Path, [string]$RunId, [string]$Tag, [string]$State) {
    $record = [ordered]@{
        runId = $RunId
        tag = $Tag
        state = $State
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $temporary = $Path + '.partial'
    try {
        [System.IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $record -Compress), [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $releaseDirectory -PathType Container)) {
    throw "Release directory is missing: $releaseDirectory"
}
if (-not (Test-Path -LiteralPath $AssetDirectory -PathType Container)) {
    throw "Verified AssetDirectory is missing: $AssetDirectory"
}
if (-not (Test-Path -LiteralPath $publisherPath -PathType Leaf)) {
    throw "Gitee mirror publisher is missing: $publisherPath"
}
if (Test-Path Env:GITEE_TOKEN) {
    throw 'GITEE_TOKEN is already set in this PowerShell process; refusing to replace or reuse it.'
}

Write-SecureMirrorStatus $statusPath $runId $Tag $state
Write-Host "GITEE_MIRROR_STATUS=$state run_id=$runId"
Write-Host "Status: $statusPath"
Write-Host "Credential-free transfer telemetry: $telemetryPath"
Write-Host 'Enter the Gitee token only at the hidden secure prompt below.'

try {
    $secureToken = Read-Host 'GITEE_TOKEN (input is hidden)' -AsSecureString
    if ($null -eq $secureToken -or $secureToken.Length -eq 0) {
        throw 'No Gitee token was entered.'
    }
    $tokenBstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    $tokenText = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenBstr)
    $env:GITEE_TOKEN = $tokenText
    $tokenText = $null

    $state = 'RUNNING'
    Write-SecureMirrorStatus $statusPath $runId $Tag $state
    Write-Host "GITEE_MIRROR_STATUS=$state run_id=$runId"
    & $publisherPath -Tag $Tag -AssetDirectory $AssetDirectory -TelemetryPath $telemetryPath
    $state = 'SUCCESS'
    Write-SecureMirrorStatus $statusPath $runId $Tag $state
    Write-Host "GITEE_MIRROR_STATUS=$state run_id=$runId"
} catch {
    $message = [string]$_.Exception.Message
    if (-not [string]::IsNullOrEmpty([string]$env:GITEE_TOKEN)) {
        $message = $message.Replace([string]$env:GITEE_TOKEN, '[redacted]')
    }
    $message = $message -replace '(?i)(access_token=)[^&\s]+', '$1[redacted]'
    $message = $message -replace '[\r\n]+', ' '
    $state = 'FAILED'
    Write-SecureMirrorStatus $statusPath $runId $Tag $state
    Write-Host "GITEE_MIRROR_STATUS=$state run_id=$runId reason=$message" -ForegroundColor Red
} finally {
    Remove-Item Env:GITEE_TOKEN -ErrorAction SilentlyContinue
    if ($tokenBstr -ne [IntPtr]::Zero) {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenBstr)
        $tokenBstr = [IntPtr]::Zero
    }
    if ($null -ne $secureToken) { $secureToken.Dispose() }
    $tokenText = $null
    $secureToken = $null
}
