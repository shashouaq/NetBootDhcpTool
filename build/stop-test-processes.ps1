$ErrorActionPreference = "SilentlyContinue"
pktmon stop | Out-Null
pktmon filter remove | Out-Null
Get-CimInstance Win32_Process |
  Where-Object {
    ($_.Name -like 'NetBootDhcpTool*') -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*NetBootDhcpTool*')
  } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Milliseconds 500
