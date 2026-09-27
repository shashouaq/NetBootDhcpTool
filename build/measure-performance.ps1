[CmdletBinding()]
param([string]$ReportPath)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportDirectory = Join-Path $repoRoot 'artifacts\performance'
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $commit = (& git -C $repoRoot rev-parse --short HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit for the performance report.' }
    $ReportPath = Join-Path $reportDirectory "performance-$commit-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
}
$reportPath = [System.IO.Path]::GetFullPath($ReportPath)
$parent = Split-Path -Parent $reportPath
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

$priorReportPath = $env:NETBOOT_PERFORMANCE_REPORT_PATH
$env:NETBOOT_PERFORMANCE_REPORT_PATH = $reportPath
try {
    & $dotnet test (Join-Path $repoRoot 'src\NetBootDhcpTool.UnitTests\NetBootDhcpTool.UnitTests.csproj') `
        -c Release --filter 'TestCategory=Performance' --output Detailed
    if ($LASTEXITCODE -ne 0) { throw "Performance measurement failed with exit code $LASTEXITCODE." }
}
finally {
    $env:NETBOOT_PERFORMANCE_REPORT_PATH = $priorReportPath
}

if (-not (Test-Path -LiteralPath $reportPath)) { throw 'The performance test did not create its report.' }
$report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
if ($report.SchemaVersion -ne 1 -or @($report.Scan254.Samples).Count -ne 5 `
    -or @($report.Scan4096.Samples).Count -ne 5 -or @($report.HttpProbe64.Samples).Count -ne 5 `
    -or @($report.SessionLog10K.Samples).Count -ne 5 -or @($report.ScanHistory4096.Samples).Count -ne 5 `
    -or @($report.LeaseProbe128.Samples).Count -ne 5 -or @($report.AdapterRefreshBurst100.Samples).Count -ne 5 `
    -or @($report.AdapterRefreshNoChange.Samples).Count -ne 5 `
    -or @($report.AdapterReadFullListReference.Samples).Count -ne 5 -or @($report.AdapterReadSelectedIdentity.Samples).Count -ne 5 `
    -or @($report.RoutePlanningFourReadBaseline.Samples).Count -ne 5 -or @($report.RoutePlanningSingleSnapshot.Samples).Count -ne 5 `
    -or @($report.PowerShellRunnerSuccess.Samples).Count -ne 5 -or @($report.PowerShellRunnerFailure.Samples).Count -ne 5 `
    -or @($report.PowerShellRunnerCancellation.Samples).Count -ne 5 -or @($report.PowerShellRunnerTimeout.Samples).Count -ne 5) {
    throw 'The performance report is incomplete or invalid.'
}
Write-Output "PERFORMANCE_REPORT_OK path=$reportPath scanSamples=5,5 httpSamples=5 logSamples=5 historySamples=5 leaseSamples=5 refreshSamples=5 noChangeSamples=5 adapterSamples=5,5 routeSamples=5,5 powerShellRunnerSamples=5,5,5,5"
