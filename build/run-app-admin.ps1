$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "release\NetBootDhcpTool\NetBootDhcpTool.exe"
if (-not (Test-Path $exe)) {
    throw "App not found. Run build\publish.ps1 first."
}
Start-Process -FilePath $exe -Verb RunAs
