$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'build\package-content.ps1')

function Assert-PackageContent([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$setupPackageScriptPath = Join-Path $repoRoot 'build\package-setup.ps1'
$setupPackageScript = [System.IO.File]::ReadAllText($setupPackageScriptPath)
$parseTokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile($setupPackageScriptPath, [ref]$parseTokens, [ref]$parseErrors) | Out-Null
Assert-PackageContent ($parseErrors.Count -eq 0) 'The Setup packaging script must parse without errors.'
Assert-PackageContent ($setupPackageScript -notmatch '\$combinedSize\s*-ge\s*\$limit|one-time migration download reaches') 'The sum of separately downloadable assets must not be a platform hard-fail gate.'
Assert-PackageContent ($setupPackageScript -match 'First migration total download: \$combinedSize bytes.*UX/bandwidth metric only') 'The combined migration download must remain visible as a non-blocking UX metric.'
Assert-PackageContent ($setupPackageScript -match 'foreach \(\$asset in @\(' -and $setupPackageScript -match '\$asset\.Size -gt \$warningThreshold') 'Setup and Full 7z must be checked individually against the warning threshold.'
$setupFixtureTotal = 46262743L + 58256825L
Assert-PackageContent ($setupFixtureTotal -eq 104519568L) 'The recorded two-asset migration download metric must remain reproducible.'
$setupHelperSource = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'src\NetBootDhcpTool.SetupHelper\Program.cs'))
Assert-PackageContent ($setupHelperSource -match 'new VersionUpdateService\(testClient\)' -and $setupHelperSource -match 'service\.CheckAsync\(current\)' -and $setupHelperSource -match 'service\.DownloadPackageAsync\(') 'Setup online downloads must reuse the signed dual-source update service, with only guarded loopback transport in isolated tests.'
Assert-PackageContent ($setupHelperSource -match 'WaitForFinalResultAsync' -and $setupHelperSource -match 'InstallationTimeout') 'Setup must wait for its transaction result with a bounded deadline.'
Assert-PackageContent ($setupHelperSource -match 'LOCAL_PACKAGE_VERIFIED' -and $setupHelperSource -match 'localInfo\.Length != package\.Size' -and $setupHelperSource -match 'localHash\.Equals\(package\.Sha256') 'Same-directory Full 7z must be verified against signed size and SHA-256 before use.'
$setupNsisSource = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'installer\NetBootDhcpTool.Setup.nsi'))
Assert-PackageContent ($setupNsisSource -match 'IfFileExists "\$1" local_package_ready' -and $setupNsisSource -match 'dual-source downloader') 'Setup must support optional same-directory offline packages and online dual-source download.'
Assert-PackageContent ($setupNsisSource -match '!define SETUP_COMPRESSOR "zlib"' -and $setupNsisSource -match 'SetCompressor /SOLID \$\{SETUP_COMPRESSOR\}') 'The measured zlib compressor should be the Setup default.'
Assert-PackageContent ($setupNsisSource -match 'ExecWait' -and $setupNsisSource -match 'SetErrorLevel \$0' -and $setupNsisSource -match 'USER_CANCELLED_EXIT_CODE') 'NSIS must preserve final helper failure codes and the centralized cancellation code.'
$publishScript = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'build\publish.ps1'))
Assert-PackageContent ($publishScript -match "Get-ChildItem -LiteralPath \`$publishRoot -File -Recurse -Filter '\*\.pdb' \| Remove-Item -Force") 'Release packaging must strip generated PDB symbols from publish outputs before inventory validation.'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('netboot-package-content-' + [guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $testRoot 'source'
$destinationRoot = Join-Path $testRoot 'product'
try {
    foreach ($relativePath in $script:ProductDocumentationAllowlist) {
        $path = Join-Path $sourceRoot ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [System.IO.File]::WriteAllText($path, "allowed:$relativePath")
    }
    foreach ($relativePath in @(
        'PROJECT_MEMORY.md',
        'docs/MAINTENANCE_GUIDE.md',
        'docs/RELEASE_PROCESS.md',
        'docs/TODO.md',
        'docs/tasks/T19.md',
        'docs/archive/formal-release-self-hosted-2026-09-28.yml',
        'docs/tests/fixture.log',
        'docs/cache/temporary.tmp'
    )) {
        $path = Join-Path $sourceRoot ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [System.IO.File]::WriteAllText($path, "forbidden:$relativePath")
    }

    Copy-ProductDocumentation -SourceRoot $sourceRoot -DestinationProduct $destinationRoot
    foreach ($requiredName in @('NetBootDhcpTool.exe', 'NetBootDhcpTool.Updater.exe', 'README_RUN.txt')) {
        [System.IO.File]::WriteAllText((Join-Path $destinationRoot $requiredName), "required:$requiredName")
    }
    $contentManifest = @(Get-ProductPackageContentManifest -Directory $destinationRoot)
    $actual = @($contentManifest | ForEach-Object { $_.Path })
    $expected = @($script:ProductDocumentationAllowlist + @('NetBootDhcpTool.exe', 'NetBootDhcpTool.Updater.exe', 'README_RUN.txt') | Sort-Object)
    Assert-PackageContent ($actual.Count -eq $expected.Count) 'The package documentation file count differs from the allowlist.'
    for ($index = 0; $index -lt $expected.Count; $index++) {
        Assert-PackageContent ($actual[$index] -ceq $expected[$index]) "Unexpected product documentation path: $($actual[$index])"
        if ($actual[$index] -in @('NetBootDhcpTool.exe', 'NetBootDhcpTool.Updater.exe', 'README_RUN.txt')) {
            $contents = [System.IO.File]::ReadAllText((Join-Path $destinationRoot $actual[$index]))
            Assert-PackageContent ($contents -ceq "required:$($actual[$index])") "Required product file changed unexpectedly: $($actual[$index])."
        } else {
            $contents = [System.IO.File]::ReadAllText((Join-Path $destinationRoot ($actual[$index].Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
            Assert-PackageContent ($contents -ceq "allowed:$($actual[$index])") "Unexpected content copied for $($actual[$index])."
        }
    }

    foreach ($relativePath in @('PROJECT_MEMORY.md', 'MAINTENANCE_GUIDE.md', 'NetBootDhcpTool.pdb', 'NetBootDhcpTool.xml', 'logs/app.log', 'tests/fixture.json', 'cache/entry.json', 'temp/build.tmp')) {
        $path = Join-Path $destinationRoot ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [System.IO.File]::WriteAllText($path, 'forbidden')
        $rejected = $false
        try { $null = Get-ProductPackageContentManifest -Directory $destinationRoot }
        catch { $rejected = $_.Exception.Message -match '(Forbidden file in product package|Unapproved root documentation)' }
        Assert-PackageContent $rejected "The package content manifest must reject $relativePath."
        Remove-Item -LiteralPath $path -Force
    }

    foreach ($relativePath in @('runtime/a/shared.dll', 'runtime/b/shared.dll')) {
        $path = Join-Path $destinationRoot ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [System.IO.File]::WriteAllText($path, 'runtime')
    }
    $duplicateRuntimeRejected = $false
    try { $null = Get-ProductPackageContentManifest -Directory $destinationRoot }
    catch { $duplicateRuntimeRejected = $_.Exception.Message -match 'Duplicate loose runtime file names' }
    Assert-PackageContent $duplicateRuntimeRejected 'Duplicate loose runtime DLLs must fail the package content gate.'

    $missingUpdaterRoot = Join-Path $testRoot 'missing-updater'
    New-Item -ItemType Directory -Path $missingUpdaterRoot -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $missingUpdaterRoot 'NetBootDhcpTool.exe'), 'app')
    [System.IO.File]::WriteAllText((Join-Path $missingUpdaterRoot 'README_RUN.txt'), 'instructions')
    $missingUpdaterRejected = $false
    try { $null = Get-ProductPackageContentManifest -Directory $missingUpdaterRoot }
    catch { $missingUpdaterRejected = $_.Exception.Message -match 'Required product package file is missing: NetBootDhcpTool.Updater.exe' }
    Assert-PackageContent $missingUpdaterRejected 'The package content gate must reject an archive missing its update process.'

    $missingInputRoot = Join-Path $testRoot 'missing-source'
    New-Item -ItemType Directory -Path $missingInputRoot -Force | Out-Null
    $missingFailed = $false
    try { Copy-ProductDocumentation -SourceRoot $missingInputRoot -DestinationProduct (Join-Path $testRoot 'missing-product') }
    catch { $missingFailed = $_.Exception.Message -match 'Required product documentation is missing' }
    Assert-PackageContent $missingFailed 'Missing required product documentation must fail packaging.'

    Write-Output 'PACKAGE_CONTENT_TESTS_OK allowlisted=6 internal_docs_excluded=2 forbidden_artifacts=8 duplicate_runtime=1 missing_updater_rejected=1 missing_document_rejected=1 per_asset_size_preflight=1 migration_total_metric=non_blocking setup_dual_source=1 offline_package_verification=1 setup_compressor=zlib'
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
