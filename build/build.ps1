$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dotnet = & (Join-Path $PSScriptRoot "resolve-dotnet.ps1")
& $dotnet restore .\NetBootDhcpTool.sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build .\NetBootDhcpTool.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
