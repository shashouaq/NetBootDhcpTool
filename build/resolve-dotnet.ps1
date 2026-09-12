$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$installer = Join-Path $PSScriptRoot "dotnet-install.ps1"
$globalJson = Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json
$requiredSdkVersion = [string]$globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($requiredSdkVersion)) { throw 'The SDK version is missing from global.json.' }
function Test-Sdk($dotnetPath) {
    if (-not (Test-Path $dotnetPath)) { return $false }
    $sdks = & $dotnetPath --list-sdks 2>$null
    return -not [string]::IsNullOrWhiteSpace($sdks)
}
function Test-RepositorySdk($dotnetPath) {
    if (-not (Test-Sdk $dotnetPath)) { return $false }
    Push-Location $root
    try {
        $resolvedVersion = & $dotnetPath --version 2>$null
        return $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($resolvedVersion -join ''))
    }
    finally { Pop-Location }
}
if (Test-RepositorySdk $localDotnet) {
    Write-Output $localDotnet
    exit 0
}
$systemDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if ($systemDotnet -and (Test-RepositorySdk $systemDotnet)) {
    Write-Output $systemDotnet
    exit 0
}
if (-not (Test-Path $installer)) {
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Version $requiredSdkVersion -InstallDir (Join-Path $root ".dotnet") -NoPath
if (-not (Test-RepositorySdk $localDotnet)) {
    throw "Pinned .NET SDK $requiredSdkVersion could not be resolved. Install that SDK or check network access for local SDK bootstrap."
}
Write-Output $localDotnet
