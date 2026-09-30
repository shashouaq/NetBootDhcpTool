[CmdletBinding()]
param([Parameter(Mandatory)][string]$TestRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$test = [IO.Path]::GetFullPath($TestRoot)
$ownedTestRoot = $false
trap {
    if ($ownedTestRoot) { [IO.File]::WriteAllText((Join-Path $test 'prepare-failed.txt'), $_.Exception.ToString()) }
    exit 1
}
if (Test-Path -LiteralPath $test) { throw 'TestRoot must be new; existing evidence is preserved.' }
if (-not $test.StartsWith('D:\Release\_t19_reliability_', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected isolated test root.' }
New-Item -ItemType Directory -Path $test | Out-Null
$ownedTestRoot = $true
[IO.File]::WriteAllText((Join-Path $test '.netboot-test-root'), 'NetBootDhcpTool isolated integration test v1')
$snapshot = Join-Path $test 'source'
New-Item -ItemType Directory -Path $snapshot | Out-Null
Push-Location $repo
try {
    $taskRg = Get-Command rg.exe -ErrorAction SilentlyContinue
    $sourceDirectories = @('src','build','installer','docs','assets') | Where-Object { Test-Path -LiteralPath (Join-Path $repo $_) }
    $files = if ($taskRg) { @(& $taskRg.Source --files --hidden @sourceDirectories) }
        else { @($sourceDirectories | ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $repo $_) -Recurse -File } | ForEach-Object { [IO.Path]::GetRelativePath($repo,$_.FullName) }) }
    $files += @('global.json','Directory.Build.props','Directory.Build.targets','NuGet.Config')
    foreach ($file in $files) {
        $source = Join-Path $repo $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or $file -match '[\\/](bin|obj)[\\/]') { continue }
        $destination = Join-Path $snapshot $file
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
    $taskDotnet = & (Join-Path $repo 'build\resolve-dotnet.ps1')
} finally { Pop-Location }
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    # Only the public half is written into the isolated source snapshot. No production source or key is changed.
    $corePath = Join-Path $snapshot 'src\NetBootDhcpTool.Core\UpdatePackages.cs'
    $core = [IO.File]::ReadAllText($corePath)
    $core = [regex]::Replace($core, '(?ms)(public const string TrustedPublicKeyPem = """\r?\n).*?(\r?\n""";)', ('${1}' + $rsa.ExportSubjectPublicKeyInfoPem() + '${2}'))
    [IO.File]::WriteAllText($corePath, $core, [Text.UTF8Encoding]::new($false))
    $env:NETBOOT_TEST_SIGNING_PRIVATE_KEY = $rsa.ExportPkcs8PrivateKeyPem()
    $env:DOTNET_ROOT = Split-Path -Parent $taskDotnet
    foreach ($project in @('src\NetBootDhcpTool.SetupHelper\NetBootDhcpTool.SetupHelper.csproj','src\NetBootDhcpTool.Updater\NetBootDhcpTool.Updater.csproj')) {
        $name = if ($project -like '*SetupHelper*') { 'helper' } else { 'updater' }
        & $taskDotnet publish (Join-Path $snapshot $project) -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:NuGetAudit=false -o (Join-Path $test $name) *> (Join-Path $test "$name-build.log")
        if ($LASTEXITCODE -ne 0) { throw "$name build failed." }
    }
    & $taskDotnet publish (Join-Path $snapshot 'build\reliability\HealthProbe.csproj') -c Release -r win-x64 --self-contained false -p:DebugType=None -o (Join-Path $test 'probe') *> (Join-Path $test 'probe-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    foreach ($targetVersion in @('1.1.0','1.1.1')) {
        $realOutput = Join-Path $test "real-product-$targetVersion"
        & $taskDotnet publish (Join-Path $snapshot 'src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$targetVersion" "-p:FileVersion=$targetVersion.0" "-p:AssemblyVersion=$targetVersion.0" -p:NuGetAudit=false -o $realOutput *> (Join-Path $test "real-product-$targetVersion-build.log")
        if ($LASTEXITCODE -ne 0) { throw "Real App $targetVersion build failed." }
        Copy-Item -LiteralPath (Join-Path $test 'updater\NetBootDhcpTool.Updater.exe') -Destination $realOutput
    }
    & $taskDotnet build (Join-Path $snapshot 'build\reliability\ProcessMatrix.csproj') -c Release -p:NuGetAudit=false *> (Join-Path $test 'matrix-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Matrix build failed.' }
    & $taskDotnet (Join-Path $snapshot 'build\reliability\bin\Release\net10.0-windows\ProcessMatrix.dll') $test *> (Join-Path $test 'matrix.log')
    $matrixExit = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $test 'matrix.exit'), [string]$matrixExit)
    exit $matrixExit
} catch {
    [IO.File]::WriteAllText((Join-Path $test 'prepare-failed.txt'), $_.Exception.ToString())
    throw
} finally {
    Remove-Item Env:NETBOOT_TEST_SIGNING_PRIVATE_KEY -ErrorAction SilentlyContinue
    $rsa.Dispose()
}
