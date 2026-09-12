$ErrorActionPreference = "SilentlyContinue"
$processes = @(Get-CimInstance Win32_Process)
$runningApp = @($processes | Where-Object {
    $_.Name -eq 'NetBootDhcpTool.exe' -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match '(?i)NetBootDhcpTool(?:\.App\.csproj|\.(?:dll|exe))(?:\W|$)')
})
if ($runningApp.Count -gt 0) {
    $ids = ($runningApp | ForEach-Object { $_.ProcessId }) -join ', '
    throw "Close the running NetBootDhcpTool app before packaging; process ID(s): $ids"
}

# Only stop this repository's test runners. PktMon captures and filters are machine-wide
# and are not owned by the test process, so packaging must leave them untouched.
$testProcesses = @($processes | Where-Object {
    $_.Name -match '^NetBootDhcpTool\.(?:Tests|UnitTests|UiTests)(?:\.exe)?$' -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match '(?i)NetBootDhcpTool\.(?:Tests|UnitTests|UiTests)\.(?:csproj|dll|exe)(?:\W|$)')
})
foreach ($process in $testProcesses) { Stop-Process -Id $process.ProcessId -Force }
Start-Sleep -Milliseconds 500
