$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot "stop-test-processes.ps1")
Get-ChildItem $root -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force
if (Test-Path (Join-Path $root "release")) { Remove-Item (Join-Path $root "release") -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $root "release") | Out-Null
