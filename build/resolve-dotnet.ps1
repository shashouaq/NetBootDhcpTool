$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$installer = Join-Path $PSScriptRoot "dotnet-install.ps1"
function Test-Sdk($dotnetPath) {
    if (-not (Test-Path $dotnetPath)) { return $false }
    $sdks = & $dotnetPath --list-sdks 2>$null
    return -not [string]::IsNullOrWhiteSpace($sdks)
}
function Test-Net8Sdk($dotnetPath) {
    if (-not (Test-Sdk $dotnetPath)) { return $false }
    $sdks = & $dotnetPath --list-sdks 2>$null
    return $sdks -match '^\s*8\.'
}
if (Test-Net8Sdk $localDotnet) {
    Write-Output $localDotnet
    exit 0
}
$systemDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if ($systemDotnet -and (Test-Net8Sdk $systemDotnet)) {
    Write-Output $systemDotnet
    exit 0
}
if (-not (Test-Path $installer)) {
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 8.0 -InstallDir (Join-Path $root ".dotnet") -NoPath
if (-not (Test-Net8Sdk $localDotnet)) {
    throw "No .NET 8 SDK found. Install .NET 8 SDK or check network access for local SDK bootstrap."
}
Write-Output $localDotnet
