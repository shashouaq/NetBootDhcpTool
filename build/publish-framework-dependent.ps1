$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root "release\NetBootDhcpTool-fd"
Set-Location $root
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
$dotnet = & (Join-Path $PSScriptRoot "resolve-dotnet.ps1")
& $dotnet publish .\src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj -c Release --self-contained false --no-restore -p:DebugType=None -p:DebugSymbols=false -o $release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish .\src\NetBootDhcpTool.DhcpVerifier\NetBootDhcpTool.DhcpVerifier.csproj -c Release --self-contained false --no-restore -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $release "tools")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item .\config $release -Recurse -Force
Copy-Item .\i18n $release -Recurse -Force
Copy-Item .\assets $release -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $release "logs") | Out-Null
Write-Host "Published: $release"
