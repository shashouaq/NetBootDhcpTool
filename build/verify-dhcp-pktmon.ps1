$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root "release\NetBootDhcpTool-tools\NetBootDhcpTool.DhcpVerifier.exe"
$outDir = Join-Path $root "logs\dhcp-verify"
$etl = Join-Path $outDir "dhcp.etl"
$txt = Join-Path $outDir "dhcp.txt"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (-not (Test-Path $tools)) {
    throw "Verifier not found. Run build\publish.ps1 first."
}
pktmon stop | Out-Null
pktmon filter remove | Out-Null
pktmon filter add DHCP67 -t UDP -p 67 | Out-Null
pktmon filter add DHCP68 -t UDP -p 68 | Out-Null
if (Test-Path $etl) { Remove-Item $etl -Force }
if (Test-Path $txt) { Remove-Item $txt -Force }
pktmon start --capture --pkt-size 0 --file-name $etl | Out-Null
try {
    & $tools 255.255.255.255
    $code = $LASTEXITCODE
}
finally {
    pktmon stop | Out-Null
    pktmon etl2txt $etl --out $txt --brief | Out-Null
    pktmon filter remove | Out-Null
}
Write-Host "Capture: $txt"
exit $code
