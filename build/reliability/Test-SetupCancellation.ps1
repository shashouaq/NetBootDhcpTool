[CmdletBinding()]
param([Parameter(Mandatory)][string]$TestRoot, [Parameter(Mandatory)][string]$FixtureRoot,
    [Parameter(Mandatory)][string]$MakensisPath)
$ErrorActionPreference = 'Stop'
$test = [IO.Path]::GetFullPath($TestRoot)
$fixture = [IO.Path]::GetFullPath($FixtureRoot)
if ($test -notlike 'D:\Release\_t19_*' -or (Test-Path -LiteralPath $test)) { throw 'Cancellation evidence requires a new isolated T19 test directory.' }
if (-not (Test-Path -LiteralPath (Join-Path $fixture '.netboot-test-root'))) { throw 'The signed test fixture is not isolated.' }
New-Item -ItemType Directory -Path $test | Out-Null
[IO.File]::WriteAllText((Join-Path $test '.netboot-test-root'), 'NetBootDhcpTool isolated integration test v1')
$env:NETBOOT_TEST_ROOT = $test
$env:NETBOOT_INTEGRATION_TEST = '1'
$env:NETBOOT_DATA_DIRECTORY = Join-Path $test 'user-data'
$env:NETBOOT_TEST_INSTALL_ROOT = Join-Path $test 'install'
$env:TEMP = Join-Path $test 'temp'
$env:TMP = $env:TEMP
foreach ($directory in @($env:NETBOOT_DATA_DIRECTORY, $env:NETBOOT_TEST_INSTALL_ROOT, $env:TEMP)) { New-Item -ItemType Directory -Path $directory | Out-Null }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$codeSource = [IO.File]::ReadAllText((Join-Path $repo 'src\NetBootDhcpTool.Core\UpdateResultProtocol.cs'))
$cancelCode = [int][regex]::Match($codeSource, 'Cancelled\s*=\s*(\d+)').Groups[1].Value
$manifest = Join-Path $fixture 'cases\success\manifest.json'
$signature = Join-Path $fixture 'cases\success\manifest.sig'
$helper = Join-Path $fixture 'helper\NetBootDhcpTool.SetupHelper.exe'
& $MakensisPath '-DPRODUCT_VERSION=1.1.0' "-DSETUP_HELPER=$helper" "-DMANIFEST=$manifest" "-DSIGNATURE=$signature" '-DFULL_PACKAGE_NAME=NetBootDhcpTool-full-v1.1.0.7z' "-DOUTPUT_DIRECTORY=$test" "-DUSER_CANCELLED_EXIT_CODE=$cancelCode" (Join-Path $repo 'installer\NetBootDhcpTool.Setup.nsi') *> (Join-Path $test 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'Cancellation Setup compilation failed.' }
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class T19CancelWindows {
 public delegate bool Callback(IntPtr window, IntPtr state);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr state);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder title, int count);
 [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr window, int id);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
 public static List<IntPtr> Owned(int pid) {
  var list=new List<IntPtr>(); EnumWindows((window,state)=> {uint owner;GetWindowThreadProcessId(window,out owner);if(owner==pid)list.Add(window);return true;},IntPtr.Zero);return list;
 }
 public static string Title(IntPtr window) {var text=new StringBuilder(512);GetWindowText(window,text,512);return text.ToString();}
}
'@
$results = @()
$exe = Join-Path $test 'NetBootDhcpTool-Setup-v1.1.0.exe'
foreach ($mode in @('language', 'welcome')) {
    $process = Start-Process $exe -WindowStyle Hidden -ArgumentList @("/D=$($env:NETBOOT_TEST_INSTALL_ROOT)") -PassThru
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $languageAccepted = $false
    $cancelSent = $false
    try {
        while (-not $process.HasExited -and $clock.Elapsed.TotalSeconds -lt 25) {
            foreach ($window in [T19CancelWindows]::Owned($process.Id)) {
                $title = [T19CancelWindows]::Title($window)
                if ($title -eq 'Installer Language') {
                    if ($mode -eq 'language' -and -not $cancelSent) {
                        [void][T19CancelWindows]::PostMessage([T19CancelWindows]::GetDlgItem($window, 2), 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
                        $cancelSent = $true
                    } elseif ($mode -eq 'welcome' -and -not $languageAccepted) {
                        [void][T19CancelWindows]::PostMessage([T19CancelWindows]::GetDlgItem($window, 1), 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
                        $languageAccepted = $true
                    }
                } elseif ($mode -eq 'welcome' -and $title -like 'NetBoot DHCP Tool*') {
                    $yes = [T19CancelWindows]::GetDlgItem($window, 6)
                    if ($yes -ne [IntPtr]::Zero) {
                        [void][T19CancelWindows]::PostMessage($yes, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
                    } elseif (-not $cancelSent -and $languageAccepted) {
                        $cancel = [T19CancelWindows]::GetDlgItem($window, 2)
                        if ($cancel -ne [IntPtr]::Zero) {
                            [void][T19CancelWindows]::PostMessage($cancel, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
                            $cancelSent = $true
                        }
                    }
                }
            }
            Start-Sleep -Milliseconds 50
            $process.Refresh()
        }
        if (-not $process.HasExited) { throw "Cancellation did not terminate Setup ($mode)." }
        $process.WaitForExit()
        if ($process.ExitCode -ne $cancelCode) { throw "Cancellation $mode returned $($process.ExitCode), expected $cancelCode." }
        if (@(Get-ChildItem -LiteralPath $env:NETBOOT_TEST_INSTALL_ROOT -File -Recurse).Count -ne 0) { throw 'Cancellation changed the installation directory.' }
        $results += [pscustomobject]@{ Mode=$mode; ExitCode=$process.ExitCode; Seconds=$clock.Elapsed.TotalSeconds; InstallationUnchanged=$true }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $test 'result.json')
