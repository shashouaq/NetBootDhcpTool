$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this acceptance script from the already-elevated PowerShell window. / 请在已提权的 PowerShell 窗口运行此验收脚本。'
}

$root = Split-Path -Parent $PSScriptRoot
$acceptanceDirectory = Join-Path $root 'artifacts\acceptance'
New-Item -ItemType Directory -Path $acceptanceDirectory -Force | Out-Null
$routeLogPath = Join-Path $acceptanceDirectory 'admin-route-smoke.log'
$adapterResultPath = Join-Path $acceptanceDirectory 't21-physical-adapter-cancel.json'
$acceptanceResultPath = Join-Path $acceptanceDirectory 'admin-acceptance.json'
$routeTestPrefixes = @(
    '198.18.250.0/24', '198.18.251.0/24', 'fd12:250:250::/64', 'fd12:250:251::/64',
    '10.250.10.0/24', '10.250.20.0/24', '10.250.30.0/24', '10.250.40.0/24',
    'fd12:250:252::/64'
)

function Get-RouteSmokeFingerprints {
    @(Get-NetRoute -ErrorAction Stop |
        Where-Object { $routeTestPrefixes -contains [string]$_.DestinationPrefix } |
        ForEach-Object { '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.DestinationPrefix, $_.InterfaceIndex, $_.NextHop, $_.RouteMetric, $_.PolicyStore, $_.InstanceId })
}

function Get-RouteSmokeResourceDelta {
    param(
        [string[]]$BeforeSwitches,
        [string[]]$BeforeAdapters,
        [string[]]$BeforeRoutes
    )

    $afterSwitches = @(Get-VMSwitch -ErrorAction Stop | Where-Object Name -like 'NetBootRouteSmoke-*' | Select-Object -ExpandProperty Name)
    $afterAdapters = @(Get-NetAdapter -ErrorAction Stop | Where-Object Name -like 'vEthernet (NetBootRouteSmoke-*' | Select-Object -ExpandProperty Name)
    $afterRoutes = @(Get-RouteSmokeFingerprints)
    [pscustomobject]@{
        Switches = @((Compare-Object -ReferenceObject $BeforeSwitches -DifferenceObject $afterSwitches | Where-Object SideIndicator -eq '=>') | Select-Object -ExpandProperty InputObject)
        Adapters = @((Compare-Object -ReferenceObject $BeforeAdapters -DifferenceObject $afterAdapters | Where-Object SideIndicator -eq '=>') | Select-Object -ExpandProperty InputObject)
        Routes = @((Compare-Object -ReferenceObject $BeforeRoutes -DifferenceObject $afterRoutes | Where-Object SideIndicator -eq '=>') | Select-Object -ExpandProperty InputObject)
    }
}

$routePassed = $false
$routeResourcesChecked = $false
$routeResourcesClean = $false
$adapterPassed = $false
$newSwitches = @()
$newAdapters = @()
$newRoutes = @()
$failure = $null
Push-Location $root
try {
    $beforeSwitches = @(Get-VMSwitch -ErrorAction Stop | Where-Object Name -like 'NetBootRouteSmoke-*' | Select-Object -ExpandProperty Name)
    $beforeAdapters = @(Get-NetAdapter -ErrorAction Stop | Where-Object Name -like 'vEthernet (NetBootRouteSmoke-*' | Select-Object -ExpandProperty Name)
    $beforeRoutes = @(Get-RouteSmokeFingerprints)
    if ($beforeSwitches.Count -or $beforeAdapters.Count -or $beforeRoutes.Count) {
        throw "Pre-existing route-smoke resources must be investigated before testing. Switches=$($beforeSwitches.Count), adapters=$($beforeAdapters.Count), routes=$($beforeRoutes.Count). No acceptance writes were started."
    }

    $routeOutput = @()
    $routeExitCode = 1
    $routeInvocationError = $null
    try {
        $routeOutput = @(& .\build\route-smoke.ps1 2>&1 | ForEach-Object { $_.ToString() })
        $routeExitCode = $LASTEXITCODE
    }
    catch {
        $routeInvocationError = $_.Exception.Message
        $routeOutput += $_.ToString()
    }
    $routeOutput | Set-Content -LiteralPath $routeLogPath -Encoding UTF8
    $routeText = $routeOutput -join "`n"
    $routeDelta = Get-RouteSmokeResourceDelta -BeforeSwitches $beforeSwitches -BeforeAdapters $beforeAdapters -BeforeRoutes $beforeRoutes
    $newSwitches = @($routeDelta.Switches)
    $newAdapters = @($routeDelta.Adapters)
    $newRoutes = @($routeDelta.Routes)
    $routeResourcesChecked = $true
    $routeResourcesClean = -not ($newSwitches.Count -or $newAdapters.Count -or $newRoutes.Count)

    if ($routeInvocationError) {
        throw "Elevated route smoke invocation failed: $routeInvocationError. Resources clean=$routeResourcesClean. See $routeLogPath"
    }
    if ($routeExitCode -ne 0 -or $routeText -notmatch 'ROUTE_SMOKE_OK interfaces=\d+,\d+') {
        throw "Elevated route smoke failed or omitted ROUTE_SMOKE_OK. Exit=$routeExitCode. Resources clean=$routeResourcesClean. See $routeLogPath"
    }
    $routePassed = $true
    if (-not $routeResourcesClean) {
        throw "The route smoke left test resources behind. Switches=$($newSwitches.Count), adapters=$($newAdapters.Count), routes=$($newRoutes.Count). See $routeLogPath"
    }

    $dotnet = (& .\build\resolve-dotnet.ps1 | Select-Object -Last 1).Trim()
    & $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release --no-build -- --isolated-adapter-cancel-smoke '863A3E86-475B-4BA1-AE14-8D7C654DF9C1' $adapterResultPath
    $adapterExitCode = $LASTEXITCODE
    if ($adapterExitCode -ne 0) { throw "The physical adapter cancellation smoke failed with exit code $adapterExitCode." }
    $adapterResult = Get-Content -LiteralPath $adapterResultPath -Raw | ConvertFrom-Json
    if (-not $adapterResult.passed -or -not $adapterResult.cancellationRequestedAfterDisableReadback -or -not $adapterResult.independentCompensationRestoredAdminStatus) {
        throw "The physical adapter report did not prove cancellation and compensation. See $adapterResultPath"
    }
    $adapterPassed = $true

    [pscustomobject]@{
        passed = $routePassed -and $adapterPassed
        routeSmoke = $routePassed
        routeResourcesChecked = $routeResourcesChecked
        routeResourcesClean = $routeResourcesClean
        newRouteSmokeSwitches = $newSwitches
        newRouteSmokeAdapters = $newAdapters
        newRouteSmokeRoutes = $newRoutes
        physicalAdapterCancelCompensation = $adapterPassed
        routeLog = $routeLogPath
        adapterReport = $adapterResultPath
        completedUtc = [DateTimeOffset]::UtcNow
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $acceptanceResultPath -Encoding UTF8

    Write-Output 'ADMIN_ACCEPTANCE_OK routeSmoke=ROUTE_SMOKE_OK resourcesClean=true physicalAdapter=T21_PHYSICAL_CANCEL_SMOKE_OK'
    Write-Output "ADMIN_ACCEPTANCE_REPORT=$acceptanceResultPath"
}
catch {
    $failure = $_.Exception.Message
    [pscustomobject]@{
        passed = $false
        routeSmoke = $routePassed
        routeResourcesChecked = $routeResourcesChecked
        routeResourcesClean = $routeResourcesClean
        physicalAdapterCancelCompensation = $adapterPassed
        newRouteSmokeSwitches = $newSwitches
        newRouteSmokeAdapters = $newAdapters
        newRouteSmokeRoutes = $newRoutes
        error = $failure
        routeLog = $routeLogPath
        adapterReport = $adapterResultPath
        completedUtc = [DateTimeOffset]::UtcNow
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $acceptanceResultPath -Encoding UTF8
    throw
}
finally {
    Pop-Location
}
