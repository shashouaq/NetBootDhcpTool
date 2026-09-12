$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $repoRoot 'build\resolve-dotnet.ps1')
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnet)) { throw 'Unable to locate a .NET 10 SDK.' }

Push-Location $repoRoot
try {
    & $dotnet run --project .\src\NetBootDhcpTool.UiTests\NetBootDhcpTool.UiTests.csproj -c Release --no-restore
    $exitCode = $LASTEXITCODE
}
finally { Pop-Location }
if ($exitCode -ne 0) { throw "Non-admin UI smoke failed with exit code $exitCode" }
