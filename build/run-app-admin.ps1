$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sourceExe = Join-Path $root "src\NetBootDhcpTool.App\bin\Release\net10.0-windows\NetBootDhcpTool.exe"
$releaseExe = Join-Path $root "release\NetBootDhcpTool\NetBootDhcpTool.exe"
if (Test-Path $sourceExe) {
    $exe = $sourceExe
} elseif (Test-Path $releaseExe) {
    $exe = $releaseExe
} else {
    throw "App not found. Build the Release project or run build\publish.ps1 first."
}

$running = @(Get-Process -Name "NetBootDhcpTool" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "Existing NetBootDhcpTool process detected; restarting preview."
    foreach ($process in $running) {
        $null = $process.CloseMainWindow()
    }
    if (-not (Wait-Process -InputObject $running -Timeout 5 -ErrorAction SilentlyContinue)) {
        $running | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 500
}

Write-Host "Starting local preview: $exe"
Start-Process -FilePath $exe -Verb RunAs
