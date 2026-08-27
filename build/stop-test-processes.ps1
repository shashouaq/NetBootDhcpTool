$ErrorActionPreference = "SilentlyContinue"
$null = pktmon stop 2>$null
$null = pktmon filter remove 2>$null
$global:LASTEXITCODE = 0
Get-CimInstance Win32_Process |
  Where-Object {
    ($_.Name -like 'NetBootDhcpTool*') -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*NetBootDhcpTool*')
  } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Milliseconds 500
