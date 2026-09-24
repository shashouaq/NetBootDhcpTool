$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repositoryRoot 'build\run-app-admin.ps1') -TestOnly
. (Join-Path $repositoryRoot 'build\stop-test-processes.ps1') -TestOnly

function Assert-ProcessSafety([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "PROCESS_SAFETY_TEST_FAILED: $Message" }
}

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('NetBoot process safety ' + [guid]::NewGuid().ToString('N'))
$sourceDirectory = Join-Path $sandbox 'src\NetBootDhcpTool.App\bin\Release\net10.0-windows'
$releaseDirectory = Join-Path $sandbox 'release\NetBootDhcpTool'
$testProjectDirectory = Join-Path $sandbox 'src\NetBootDhcpTool.Tests'
$testOutputDirectory = Join-Path $testProjectDirectory 'bin\Release\net10.0-windows'
New-Item -ItemType Directory -Path $sourceDirectory, $releaseDirectory, $testOutputDirectory -Force | Out-Null
$sourceExe = Join-Path $sourceDirectory 'NetBootDhcpTool.exe'
$sourceDll = Join-Path $sourceDirectory 'NetBootDhcpTool.dll'
$releaseExe = Join-Path $releaseDirectory 'NetBootDhcpTool.exe'
$testExe = Join-Path $testOutputDirectory 'NetBootDhcpTool.Tests.exe'
$testDll = Join-Path $testOutputDirectory 'NetBootDhcpTool.Tests.dll'
foreach ($path in @($sourceExe, $sourceDll, $releaseExe, $testExe, $testDll)) { Set-Content -LiteralPath $path -Value 'test fixture' }

try {
    $previewTarget = Resolve-NetBootPreviewTarget $sandbox
    Assert-ProcessSafety ($previewTarget.Executable -ieq $sourceExe) 'source Release output was not preferred for Preview'
    $disabledSourceExe = $sourceExe + '.disabled'
    Move-Item -LiteralPath $sourceExe -Destination $disabledSourceExe
    try {
        $previewTarget = Resolve-NetBootPreviewTarget $sandbox
        Assert-ProcessSafety ($previewTarget.Executable -ieq $releaseExe) 'packaged fallback was not selected when source output was absent'
    } finally { Move-Item -LiteralPath $disabledSourceExe -Destination $sourceExe }

    $dotnetProcess = [pscustomobject]@{
        Name = 'dotnet.exe'; ProcessId = 11; ExecutablePath = 'C:\Program Files\dotnet\dotnet.exe'
        CommandLine = '"C:\Program Files\dotnet\dotnet.exe" "' + $testDll + '"'
        CreationDate = [datetime]::UtcNow
    }
    Assert-ProcessSafety ((Get-NetBootProcessEntryPointPath $dotnetProcess) -ieq $testDll) 'dotnet entry point with spaces was not parsed as one absolute path'
    Assert-ProcessSafety ((Get-NetBootRepositoryTestProject $dotnetProcess $sandbox) -eq 'NetBootDhcpTool.Tests') 'the current repository test runner was not identified'
    Assert-ProcessSafety (@(Get-NetBootRunningAppProcesses @($dotnetProcess)).Count -eq 0) 'an unrelated .NET test runner was misidentified as the GUI app'
    $noAppAccepted = $true
    try { Assert-NetBootNoRunningApp @($dotnetProcess) } catch { $noAppAccepted = $false }
    Assert-ProcessSafety $noAppAccepted 'the packaging guard refused an empty app-process result'
    Assert-ProcessSafety (-not (Test-NetBootPathWithin ($sandbox + '-other\src\NetBootDhcpTool.Tests\bin\NetBootDhcpTool.Tests.exe') $sandbox)) 'a sibling path containing the repository name passed the boundary check'

    $ownedApp = [pscustomobject]@{ Name = 'NetBootDhcpTool.exe'; ProcessId = 21; ExecutablePath = $sourceExe; CommandLine = '"' + $sourceExe + '"'; CreationDate = [datetime]::UtcNow }
    $events = [System.Collections.Generic.List[string]]::new()
    $shell = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if (-not $shell) { $shell = Get-Command pwsh.exe -ErrorAction Stop }
    $slowChild = Start-Process -FilePath $shell.Source -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 6') -WindowStyle Hidden -PassThru
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Invoke-NetBootAppPreviewRestart -RepositoryRoot $sandbox -PreviewExecutable $sourceExe -OwnedAppEntryPoints @($sourceExe, $sourceDll) `
        -GetProcesses { return @($ownedApp) } `
        -CloseWindow { param($processId) [void]$events.Add("close:$processId"); return $true } `
        -WaitForExit { param($processId, $timeoutMilliseconds) [void]$events.Add("wait:$timeoutMilliseconds"); if ($timeoutMilliseconds -eq 0) { return $false }; return $slowChild.WaitForExit($timeoutMilliseconds) } `
        -StartPreview { param($path) [void]$events.Add("start:$path") }
    $clock.Stop()
    Assert-ProcessSafety ($clock.Elapsed.TotalSeconds -ge 5) 'preview restart did not wait for a simulated cleanup taking longer than five seconds'
    Assert-ProcessSafety ($events.Count -eq 4 -and $events[0] -eq 'wait:0' -and $events[1] -eq 'close:21' -and $events[2].StartsWith('wait:') -and $events[3] -eq "start:$sourceExe") 'preview was not started after the successful close wait'
    Assert-ProcessSafety ([int]($events[2].Split(':')[1]) -ge 180000) 'normal-close timeout does not cover serialized application cleanup'

    $startEvents = [System.Collections.Generic.List[string]]::new()
    $refused = $false
    try {
        Invoke-NetBootAppPreviewRestart -RepositoryRoot $sandbox -PreviewExecutable $sourceExe -OwnedAppEntryPoints @($sourceExe) `
            -GetProcesses { return @($ownedApp) } -CloseWindow { return $false } -WaitForExit { return $false } `
            -StartPreview { [void]$startEvents.Add('start') }
    } catch { $refused = $true }
    Assert-ProcessSafety ($refused -and $startEvents.Count -eq 0) 'a process refusing close was followed by a new preview'

    $timeoutEvents = [System.Collections.Generic.List[string]]::new()
    $timedOut = $false
    try {
        Invoke-NetBootAppPreviewRestart -RepositoryRoot $sandbox -PreviewExecutable $sourceExe -OwnedAppEntryPoints @($sourceExe) `
            -GetProcesses { return @($ownedApp) } -CloseWindow { return $true } -WaitForExit { return $false } `
            -StartPreview { [void]$timeoutEvents.Add('start') } -ExitTimeoutSeconds 1
    } catch { $timedOut = $true }
    Assert-ProcessSafety ($timedOut -and $timeoutEvents.Count -eq 0) 'a cleanup timeout was followed by a new preview'

    $exitedEvents = [System.Collections.Generic.List[string]]::new()
    Invoke-NetBootAppPreviewRestart -RepositoryRoot $sandbox -PreviewExecutable $sourceExe -OwnedAppEntryPoints @($sourceExe) `
        -GetProcesses { return @($ownedApp) } -CloseWindow { throw 'already-exited process must not receive close' } `
        -WaitForExit { return $true } -StartPreview { [void]$exitedEvents.Add('start') }
    Assert-ProcessSafety ($exitedEvents.Count -eq 1) 'an already-exited app process blocked preview startup'

    $foreignApp = [pscustomobject]@{ Name = 'NetBootDhcpTool.exe'; ProcessId = 22; ExecutablePath = (Join-Path $sandbox 'another-worktree\NetBootDhcpTool.exe'); CommandLine = 'foreign'; CreationDate = [datetime]::UtcNow }
    $foreignEvents = [System.Collections.Generic.List[string]]::new()
    Invoke-NetBootAppPreviewRestart -RepositoryRoot $sandbox -PreviewExecutable $sourceExe -OwnedAppEntryPoints @($sourceExe) `
        -GetProcesses { return @($foreignApp) } -CloseWindow { throw 'foreign workspace must not be closed' } `
        -WaitForExit { return $false } -StartPreview { [void]$foreignEvents.Add('start') }
    Assert-ProcessSafety ($foreignEvents.Count -eq 1) 'foreign same-name GUI process was not left untouched'

    $unreadableApp = [pscustomobject]@{ Name = 'NetBootDhcpTool.exe'; ProcessId = 23; ExecutablePath = $null; CommandLine = 'unknown'; CreationDate = [datetime]::UtcNow }
    $unreadableEvents = [System.Collections.Generic.List[string]]::new()
    $unreadableFailed = $false
    try {
        Invoke-NetBootAppPreviewRestart -RepositoryRoot $sandbox -PreviewExecutable $sourceExe -OwnedAppEntryPoints @($sourceExe) `
            -GetProcesses { return @($unreadableApp) } -StartPreview { [void]$unreadableEvents.Add('start') }
    } catch { $unreadableFailed = $true }
    Assert-ProcessSafety ($unreadableFailed -and $unreadableEvents.Count -eq 0) 'unreadable GUI ownership did not fail closed'

    $repoTest = [pscustomobject]@{ Name = 'NetBootDhcpTool.Tests.exe'; ProcessId = 31; ExecutablePath = $testExe; CommandLine = '"' + $testExe + '"'; CreationDate = [datetime]::UtcNow }
    $foreignTestPath = Join-Path $sandbox 'other\src\NetBootDhcpTool.UiTests\bin\NetBootDhcpTool.UiTests.exe'
    $foreignTest = [pscustomobject]@{ Name = 'NetBootDhcpTool.UiTests.exe'; ProcessId = 32; ExecutablePath = $foreignTestPath; CommandLine = 'foreign'; CreationDate = [datetime]::UtcNow }
    $dotnetTest = [pscustomobject]@{ Name = 'dotnet.exe'; ProcessId = 33; ExecutablePath = 'C:\Program Files\dotnet\dotnet.exe'; CommandLine = '"C:\Program Files\dotnet\dotnet.exe" "' + $testDll + '"'; CreationDate = [datetime]::UtcNow }
    $processes = @($repoTest, $foreignTest, $dotnetTest)
    $stopIds = [System.Collections.Generic.List[int]]::new()
    Invoke-NetBootStopTestProcesses -RepositoryRoot $sandbox -GetProcesses { return $processes } `
        -GetProcessById { param($processId) return ($processes | Where-Object ProcessId -eq $processId | Select-Object -First 1) } `
        -StopProcessById { param($processId) [void]$stopIds.Add($processId) } -WaitForExit { return $true } -WhatIf
    Assert-ProcessSafety ($stopIds.Count -eq 0) 'WhatIf stopped a process'
    Invoke-NetBootStopTestProcesses -RepositoryRoot $sandbox -GetProcesses { return $processes } `
        -GetProcessById { param($processId) return ($processes | Where-Object ProcessId -eq $processId | Select-Object -First 1) } `
        -StopProcessById { param($processId) [void]$stopIds.Add($processId) } -WaitForExit { return $true }
    Assert-ProcessSafety ($stopIds.Count -eq 2 -and $stopIds -contains 31 -and $stopIds -contains 33 -and $stopIds -notcontains 32) "cleanup target escaped the repository test-project boundary: $($stopIds -join ',')"

    $foreignGui = [pscustomobject]@{ Name = 'NetBootDhcpTool.exe'; ProcessId = 41; ExecutablePath = $foreignApp.ExecutablePath; CommandLine = 'foreign' }
    Assert-ProcessSafety (@(Get-NetBootRunningAppProcesses @($foreignGui)).Count -eq 1) 'packaging did not recognize a GUI app outside this workspace'
    $packageRefused = $false
    try { Assert-NetBootNoRunningApp @($foreignGui) } catch { $packageRefused = $_.Exception.Message.Contains('no process was terminated') }
    Assert-ProcessSafety $packageRefused 'package guard did not refuse a running GUI process'

    if ($slowChild -and -not $slowChild.HasExited) {
        if (-not $slowChild.WaitForExit(10000)) { throw 'slow cleanup fixture did not exit naturally' }
    }
    Write-Output 'PROCESS_SAFETY_OK: empty app-process result, scoped app path, >5s close wait, refuse/timeout/already-exited ordering, foreign same-name process, unreadable ownership, dotnet quoting, package GUI refusal, and repository-only test cleanup verified.'
}
finally {
    if ($slowChild -and -not $slowChild.HasExited) { [void]$slowChild.WaitForExit(10000) }
    if (Test-Path -LiteralPath $sandbox) {
        $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedSandbox.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to clean test directory outside temp: $resolvedSandbox" }
        Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force
    }
}
