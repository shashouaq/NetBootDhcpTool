[CmdletBinding()]
param([switch]$TestOnly)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'process-safety.ps1')

function Invoke-NetBootStopTestProcesses {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [scriptblock]$GetProcesses = { Get-CimInstance Win32_Process },
        [scriptblock]$GetProcessById = { param($processId) Get-CimInstance Win32_Process -Filter "ProcessId=$processId" },
        [scriptblock]$StopProcessById = { param($processId) Stop-Process -Id $processId -Force -ErrorAction Stop },
        [scriptblock]$WaitForExit = { param($processId) $process = Get-Process -Id $processId -ErrorAction SilentlyContinue; if (-not $process) { return $true }; $exited = $process.WaitForExit(5000); $process.Refresh(); return ($exited -or $process.HasExited) }
    )

    $root = Get-NetBootNormalizedPath $RepositoryRoot
    if (-not $root) { throw "Repository root cannot be normalized: $RepositoryRoot" }
    $snapshot = @(& $GetProcesses)

    foreach ($candidate in $snapshot) {
        if ($candidate.Name -ine 'dotnet.exe' -and $candidate.Name -notmatch '(?i)^NetBootDhcpTool\.(?:Tests|UnitTests|UiTests)\.exe$') { continue }
        if ($candidate.Name -ine 'dotnet.exe' -and [string]::IsNullOrWhiteSpace([string]$candidate.ExecutablePath)) {
            Write-Warning "Cannot read test-runner path for process $($candidate.ProcessId); it was left running."
            continue
        }
        $projectName = Get-NetBootRepositoryTestProject $candidate $root
        if (-not $projectName) { continue }
        $processId = [int]$candidate.ProcessId
        $fresh = @(& $GetProcessById $processId | Select-Object -First 1)
        if ($fresh.Count -eq 0) { continue }
        $freshProject = Get-NetBootRepositoryTestProject $fresh[0] $root
        if ($freshProject -ne $projectName) {
            Write-Warning "Test process $processId changed identity during enumeration; it was left running."
            continue
        }
        if ($candidate.CreationDate -and $fresh[0].CreationDate -and $candidate.CreationDate -ne $fresh[0].CreationDate) {
            Write-Warning "Process ID $processId was reused during enumeration; the replacement process was left running."
            continue
        }
        if ($PSCmdlet.ShouldProcess("$($fresh[0].Name) PID $processId ($projectName in this repository)", 'Stop test runner')) {
            & $StopProcessById $processId
            if (-not [bool](& $WaitForExit $processId)) { throw "Repository test process $processId did not exit after cleanup." }
        }
    }
}

if (-not $TestOnly) {
    $root = Split-Path -Parent $PSScriptRoot
    $processes = @(Get-CimInstance Win32_Process)
    Assert-NetBootNoRunningApp $processes

    Invoke-NetBootStopTestProcesses -RepositoryRoot $root
}
