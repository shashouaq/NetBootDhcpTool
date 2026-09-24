if (-not ("NetBootDhcpTool.ProcessSafety.CommandLine" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace NetBootDhcpTool.ProcessSafety {
    public static class CommandLine {
        [DllImport("shell32.dll", SetLastError = true)]
        public static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);
    }
}
'@
}

function Get-NetBootCommandLineArgumentList([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return @() }
    $count = 0
    $argv = [NetBootDhcpTool.ProcessSafety.CommandLine]::CommandLineToArgvW($CommandLine, [ref]$count)
    if ($argv -eq [IntPtr]::Zero) { throw "Windows could not parse a process command line." }
    try {
        $values = [System.Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt $count; $i++) {
            $pointer = [Runtime.InteropServices.Marshal]::ReadIntPtr($argv, $i * [IntPtr]::Size)
            $values.Add([Runtime.InteropServices.Marshal]::PtrToStringUni($pointer))
        }
        return ,$values.ToArray()
    }
    finally { [void][NetBootDhcpTool.ProcessSafety.CommandLine]::LocalFree($argv) }
}

function Get-NetBootNormalizedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)) { return $null }
    try {
        $full = [IO.Path]::GetFullPath($Path)
        if (Test-Path -LiteralPath $full) {
            $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $null }
            $full = $item.FullName
        }
        return $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    catch { return $null }
}

function Test-NetBootPathWithin([string]$Path, [string]$Directory) {
    $candidate = Get-NetBootNormalizedPath $Path
    $root = Get-NetBootNormalizedPath $Directory
    if (-not $candidate -or -not $root) { return $false }
    return $candidate.StartsWith(($root + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)
}

function Get-NetBootProcessEntryPointPath($ProcessRecord) {
    $name = [string]$ProcessRecord.Name
    $imagePath = Get-NetBootNormalizedPath ([string]$ProcessRecord.ExecutablePath)
    if ($name -ine 'dotnet.exe') {
        if ($imagePath) { return $imagePath }
        $arguments = Get-NetBootCommandLineArgumentList ([string]$ProcessRecord.CommandLine)
        if ($arguments.Count -gt 0 -and [IO.Path]::GetFileName($arguments[0]) -ieq $name) {
            return Get-NetBootNormalizedPath $arguments[0]
        }
        return $null
    }

    $arguments = Get-NetBootCommandLineArgumentList ([string]$ProcessRecord.CommandLine)
    $entryPattern = '(?i)^NetBootDhcpTool(?:\.App|\.(?:Tests|UnitTests|UiTests))?\.(?:dll|exe|csproj)$'
    foreach ($argument in $arguments) {
        if ([IO.Path]::GetFileName($argument) -notmatch $entryPattern) { continue }
        $resolved = Get-NetBootNormalizedPath $argument
        if (-not $resolved) { throw "Cannot prove the full path of NetBootDhcpTool dotnet entry point '$argument'." }
        return $resolved
    }
    return $null
}

function Get-NetBootRepositoryTestProject($ProcessRecord, [string]$RepositoryRoot) {
    $entry = Get-NetBootProcessEntryPointPath $ProcessRecord
    if (-not $entry) { return $null }
    $name = [IO.Path]::GetFileNameWithoutExtension($entry)
    if ($name -notmatch '^NetBootDhcpTool\.(Tests|UnitTests|UiTests)$') { return $null }
    $projectName = "NetBootDhcpTool.$($Matches[1])"
    $projectRoot = Join-Path $RepositoryRoot "src\$projectName"
    if ($entry -ieq (Join-Path $projectRoot "$projectName.csproj")) { return $projectName }
    if (([IO.Path]::GetExtension($entry) -in @('.dll', '.exe')) -and (Test-NetBootPathWithin $entry (Join-Path $projectRoot 'bin'))) { return $projectName }
    return $null
}

function Get-NetBootRunningAppProcesses([object[]]$Processes) {
    $running = [System.Collections.Generic.List[object]]::new()
    foreach ($process in $Processes | Where-Object { $_.Name -ieq 'NetBootDhcpTool.exe' -or $_.Name -ieq 'dotnet.exe' }) {
        $entry = Get-NetBootProcessEntryPointPath $process
        if (-not $entry) {
            if ($process.Name -ieq 'NetBootDhcpTool.exe') {
                throw "Cannot read executable path for GUI process $($process.ProcessId); its ownership is unknown."
            }
            continue
        }
        if ([IO.Path]::GetFileName($entry) -match '(?i)^NetBootDhcpTool(?:\.App)?\.(?:exe|dll)$') {
            $running.Add([pscustomobject]@{ Process = $process; EntryPoint = $entry })
        }
    }
    return $running.ToArray()
}

function Assert-NetBootNoRunningApp([object[]]$Processes) {
    $running = @(Get-NetBootRunningAppProcesses $Processes)
    if ($running.Count -eq 0) { return }
    $details = foreach ($record in $running) { "PID $($record.Process.ProcessId): $($record.EntryPoint)" }
    throw "Close the running NetBootDhcpTool app normally before packaging; no process was terminated. $($details -join '; ')"
}

function Get-NetBootRunningAppProcesses([object[]]$Processes) {
    $running = [System.Collections.Generic.List[object]]::new()
    foreach ($process in $Processes | Where-Object { $_.Name -ieq 'NetBootDhcpTool.exe' -or $_.Name -ieq 'dotnet.exe' }) {
        $entry = Get-NetBootProcessEntryPointPath $process
        if (-not $entry) {
            if ($process.Name -ieq 'NetBootDhcpTool.exe') {
                throw "Cannot read executable path for GUI process $($process.ProcessId); its ownership is unknown."
            }
            continue
        }
        if ([IO.Path]::GetFileName($entry) -match '(?i)^NetBootDhcpTool(?:\.App)?\.(?:exe|dll)$') {
            $running.Add([pscustomobject]@{ Process = $process; EntryPoint = $entry })
        }
    }
    return $running.ToArray()
}

function Invoke-NetBootAppPreviewRestart {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$PreviewExecutable,
        [Parameter(Mandatory)][string[]]$OwnedAppEntryPoints,
        [scriptblock]$GetProcesses = { Get-CimInstance Win32_Process },
        [scriptblock]$CloseWindow = { param($processId) $process = Get-Process -Id $processId -ErrorAction Stop; $process.CloseMainWindow() },
        [scriptblock]$WaitForExit = { param($processId, $timeoutMilliseconds) $process = Get-Process -Id $processId -ErrorAction SilentlyContinue; if (-not $process) { return $true }; $exited = $process.WaitForExit($timeoutMilliseconds); $process.Refresh(); return ($exited -or $process.HasExited) },
        [scriptblock]$StartPreview = { param($path) Start-Process -FilePath $path -Verb RunAs | Out-Null },
        [int]$ExitTimeoutSeconds = 240
    )

    $root = Get-NetBootNormalizedPath $RepositoryRoot
    if (-not $root) { throw "Repository root cannot be normalized: $RepositoryRoot" }
    $ownedPaths = @($OwnedAppEntryPoints | ForEach-Object { Get-NetBootNormalizedPath $_ } | Where-Object { $_ })
    $candidates = @(& $GetProcesses | Where-Object { $_.Name -ieq 'NetBootDhcpTool.exe' -or $_.Name -ieq 'dotnet.exe' })
    $ownedProcesses = [System.Collections.Generic.List[object]]::new()
    foreach ($candidate in $candidates) {
        $entry = Get-NetBootProcessEntryPointPath $candidate
        if (-not $entry) {
            if ($candidate.Name -ieq 'NetBootDhcpTool.exe') { throw "Cannot read the executable path for process $($candidate.ProcessId); preview was not started." }
            continue
        }
        $base = [IO.Path]::GetFileName($entry)
        if ($base -notmatch '(?i)^NetBootDhcpTool(?:\.App)?\.(?:exe|dll)$') { continue }
        if ($entry -in $ownedPaths) {
            $ownedProcesses.Add($candidate)
        }
        elseif (Test-NetBootPathWithin $entry $root) {
            Write-Warning "NetBootDhcpTool is running from an unrecognized build directory; it was left untouched: $entry"
        }
        else {
            Write-Warning "NetBootDhcpTool is running outside this repository and was left untouched: $entry"
        }
    }

    foreach ($candidate in $ownedProcesses) {
        $processId = [int]$candidate.ProcessId
        $alreadyExited = $false
        try { $alreadyExited = [bool](& $WaitForExit $processId 0) }
        catch { throw "Could not confirm whether NetBootDhcpTool process $processId has exited: $($_.Exception.Message)" }
        if ($alreadyExited) { continue }
        if (-not [bool](& $CloseWindow $processId)) {
            throw "The current NetBootDhcpTool process $processId refused a normal close request; no new preview was started."
        }
        if ($ExitTimeoutSeconds -gt ([int]::MaxValue / 1000)) { throw 'Exit timeout is too large.' }
        $timeoutMilliseconds = $ExitTimeoutSeconds * 1000
        if (-not [bool](& $WaitForExit $processId $timeoutMilliseconds)) {
            throw "The current NetBootDhcpTool process $processId did not finish cleanup within $ExitTimeoutSeconds seconds; it remains running and no new preview was started."
        }
    }

    & $StartPreview $PreviewExecutable
}
