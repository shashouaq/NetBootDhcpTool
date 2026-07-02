$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$installer = Join-Path $PSScriptRoot "dotnet-install.ps1"
function Test-Sdk($dotnetPath) {
    if (-not (Test-Path $dotnetPath)) { return $false }
    $sdks = & $dotnetPath --list-sdks 2>$null
    return -not [string]::IsNullOrWhiteSpace($sdks)
}
if (Test-Sdk $localDotnet) {
    Write-Output $localDotnet
    exit 0
}
$systemDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if ($systemDotnet -and (Test-Sdk $systemDotnet)) {
    Write-Output $systemDotnet
    exit 0
}
if (-not (Test-Path $installer)) {
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 8.0 -InstallDir (Join-Path $root ".dotnet") -NoPath
if (-not (Test-Sdk $localDotnet)) {
    throw "No .NET SDK found. Install .NET 8 SDK or check network access for local SDK bootstrap."
}
Write-Output $localDotnet
