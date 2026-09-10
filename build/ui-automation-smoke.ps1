$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Pinned dotnet not found: $dotnet" }

& $dotnet run --project (Join-Path $repoRoot 'src\NetBootDhcpTool.UiTests\NetBootDhcpTool.UiTests.csproj') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Non-admin UI smoke failed with exit code $LASTEXITCODE" }
