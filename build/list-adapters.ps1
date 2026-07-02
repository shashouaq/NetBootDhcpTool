$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dotnet = & (Join-Path $PSScriptRoot "resolve-dotnet.ps1")
& $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release --no-build -- --adapters
