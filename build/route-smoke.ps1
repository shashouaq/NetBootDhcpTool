$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this isolated route smoke test from an elevated PowerShell window. / 请在管理员 PowerShell 中运行此隔离路由烟测。"
}

$root = Split-Path -Parent $PSScriptRoot
$resolveDotnet = Join-Path $PSScriptRoot "resolve-dotnet.ps1"
$dotnet = (& $resolveDotnet | Select-Object -Last 1).Trim()
$switchA = "NetBootRouteSmoke-A-$PID"
$switchB = "NetBootRouteSmoke-B-$PID"
$adapterA = "vEthernet ($switchA)"
$adapterB = "vEthernet ($switchB)"
$createdSwitches = @()

try {
    New-VMSwitch -Name $switchA -SwitchType Internal -ErrorAction Stop | Out-Null
    $createdSwitches += $switchA
    New-VMSwitch -Name $switchB -SwitchType Internal -ErrorAction Stop | Out-Null
    $createdSwitches += $switchB

    $deadline = (Get-Date).AddSeconds(20)
    do {
        $nicA = Get-NetAdapter -Name $adapterA -ErrorAction SilentlyContinue
        $nicB = Get-NetAdapter -Name $adapterB -ErrorAction SilentlyContinue
        if ($nicA -and $nicB) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if (-not $nicA -or -not $nicB) { throw "The isolated vEthernet adapters did not appear in time. / 隔离 vEthernet 网卡未及时出现。" }

    New-NetIPAddress -InterfaceIndex $nicA.ifIndex -IPAddress "198.18.250.1" -PrefixLength 24 -ErrorAction Stop | Out-Null
    New-NetIPAddress -InterfaceIndex $nicB.ifIndex -IPAddress "198.18.251.1" -PrefixLength 24 -ErrorAction Stop | Out-Null
    New-NetIPAddress -InterfaceIndex $nicA.ifIndex -IPAddress "fd12:250:250::1" -PrefixLength 64 -AddressFamily IPv6 -ErrorAction Stop | Out-Null
    New-NetIPAddress -InterfaceIndex $nicB.ifIndex -IPAddress "fd12:250:251::1" -PrefixLength 64 -AddressFamily IPv6 -ErrorAction Stop | Out-Null

    Push-Location $root
    try {
        & $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release --no-restore -- --route-smoke $nicA.ifIndex $nicB.ifIndex
        if ($LASTEXITCODE -ne 0) { throw "Route smoke test failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
}
finally {
    foreach ($switchName in $createdSwitches) {
        Remove-VMSwitch -Name $switchName -Force -ErrorAction SilentlyContinue
    }
}
