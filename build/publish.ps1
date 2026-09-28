param(
    [string]$GitHubRepository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'shashouaq/NetBootDhcpTool' }),
    [string]$OutputDirectory,
    [string]$BaselineDirectory
)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repositoryReleaseRoot = Join-Path $root 'release'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryReleaseRoot ('local-build-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
}
$releaseRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $releaseRoot) { throw "OutputDirectory must be a new, empty path. Existing output is preserved: $releaseRoot" }
if ([string]::IsNullOrWhiteSpace($BaselineDirectory)) { $BaselineDirectory = $repositoryReleaseRoot }
$BaselineDirectory = [System.IO.Path]::GetFullPath($BaselineDirectory)
$product = Join-Path $releaseRoot 'NetBootDhcpTool'
$toolsRelease = Join-Path $releaseRoot 'NetBootDhcpTool-tools'
$projectFile = Join-Path $root 'src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj'
$updaterProject = Join-Path $root 'src\NetBootDhcpTool.Updater\NetBootDhcpTool.Updater.csproj'
[xml]$projectXml = Get-Content -LiteralPath $projectFile
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version not found or invalid in $projectFile" }
$tag = "v$version"
$versionedRelease = Join-Path $releaseRoot "NetBootDhcpTool-v$version"
$archive = Join-Path $releaseRoot "NetBootDhcpTool-v$version.7z"
$fullPackageName = "NetBootDhcpTool-full-v$version.zip"
$fullPackage = Join-Path $releaseRoot $fullPackageName
$manifestPath = Join-Path $releaseRoot 'latest.json'
$releaseNotesPath = Join-Path $root 'docs\RELEASE_NOTES.md'
$changeLogPath = Join-Path $root 'docs\FEATURE_CHANGELOG.md'
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'

function Remove-OwnedOutput([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if ([System.IO.Path]::GetDirectoryName($full) -cne $parent) { throw "Refusing to remove output outside the release directory: $full" }
    Remove-Item -LiteralPath $full -Recurse -Force
}

function Get-ProductFiles([string]$Directory) {
    $base = [System.IO.Path]::GetFullPath($Directory).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse -Force) {
        $relative = $file.FullName.Substring($base.Length).Replace('\', '/')
        if ($relative.Equals('install-manifest.json', [System.StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('logs/', [System.StringComparison]::OrdinalIgnoreCase) -or
            $relative.EndsWith('.tmp', [System.StringComparison]::OrdinalIgnoreCase) -or
            $relative.EndsWith('.bak', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        [pscustomobject]@{
            Path = $relative
            SourcePath = $file.FullName
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            Size = [long]$file.Length
        }
    }
}

function New-PackageZip([string]$Path, [System.Collections.IDictionary]$PackageDocument, [object[]]$Files) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $metadataEntry = $zip.CreateEntry('update-package.json', [System.IO.Compression.CompressionLevel]::Optimal)
        $metadataStream = $metadataEntry.Open()
        try {
            $writer = [System.IO.StreamWriter]::new($metadataStream, [System.Text.UTF8Encoding]::new($false))
            try { $writer.Write((ConvertTo-Json -InputObject $PackageDocument -Depth 12)) }
            finally { $writer.Dispose() }
        } finally { $metadataStream.Dispose() }
        foreach ($file in $Files) {
            $entry = $zip.CreateEntry("payload/$($file.Path)", [System.IO.Compression.CompressionLevel]::Optimal)
            $input = [System.IO.File]::OpenRead($file.SourcePath)
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $zip.Dispose() }
}

function Write-Checksum([string]$Path) {
    $name = [System.IO.Path]::GetFileName($Path)
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$Path.sha256", "$hash  $name", [System.Text.Encoding]::ASCII)
    return $hash
}

function Read-InstallManifest([string]$Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $releaseRoot) | Out-Null
New-Item -ItemType Directory -Path $releaseRoot | Out-Null
Set-Location $root
& (Join-Path $PSScriptRoot 'stop-test-processes.ps1')
if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) { throw "7-Zip not found: $sevenZip" }
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1')
& $dotnet restore .\NetBootDhcpTool.sln -r win-x64 --configfile .\NuGet.config --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish .\src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $product
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$updaterOutput = Join-Path $releaseRoot ('.updater-publish-' + [guid]::NewGuid().ToString('N'))
try {
    & $dotnet publish .\src\NetBootDhcpTool.Updater\NetBootDhcpTool.Updater.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $updaterOutput
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Copy-Item -LiteralPath (Join-Path $updaterOutput 'NetBootDhcpTool.Updater.exe') -Destination (Join-Path $product 'NetBootDhcpTool.Updater.exe') -Force
} finally { Remove-OwnedOutput $updaterOutput }
& $dotnet publish .\src\NetBootDhcpTool.DhcpVerifier\NetBootDhcpTool.DhcpVerifier.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $toolsRelease
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($directory in @('config', 'i18n', 'assets', 'docs')) { Copy-Item -LiteralPath (Join-Path $root $directory) -Destination $product -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $product -Force
Copy-Item -LiteralPath (Join-Path $root 'PROJECT_MEMORY.md') -Destination $product -Force
[System.IO.File]::WriteAllText((Join-Path $product 'README_RUN.txt'), "Run NetBootDhcpTool.exe to start the application.`r`nYou may move this NetBootDhcpTool folder to any writable path.`r`nNetBootDhcpTool-tools contains command-line diagnostic tools only.", [System.Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Force -Path (Join-Path $product 'logs') | Out-Null

$productFiles = @(Get-ProductFiles $product | Sort-Object Path)
$installManifest = [ordered]@{
    schemaVersion = 1
    productId = 'NetBootDhcpTool'
    version = $version
    entryPoint = 'NetBootDhcpTool.exe'
    files = @($productFiles | ForEach-Object { [ordered]@{ path=$_.Path;sha256=$_.Sha256;size=$_.Size } })
}
$installManifestPath = Join-Path $product 'install-manifest.json'
[System.IO.File]::WriteAllText($installManifestPath, (ConvertTo-Json -InputObject $installManifest -Depth 10), [System.Text.UTF8Encoding]::new($false))
$manifestFile = [pscustomobject]@{ Path='install-manifest.json';SourcePath=$installManifestPath;Sha256=(Get-FileHash -LiteralPath $installManifestPath -Algorithm SHA256).Hash.ToLowerInvariant();Size=[long](Get-Item -LiteralPath $installManifestPath).Length }
$fullFiles = @($productFiles) + @($manifestFile)
$fullPackageEntries = @($fullFiles | ForEach-Object { [ordered]@{path=$_.Path;sha256=$_.Sha256;size=$_.Size} })
$fullDocument = [ordered]@{schemaVersion=1;productId='NetBootDhcpTool';kind='Full';targetVersion=$version;baseVersion=$null;baseInstallManifestSha256=$null;files=$fullPackageEntries;deletedFiles=@()}
New-PackageZip -Path $fullPackage -PackageDocument $fullDocument -Files $fullFiles
$fullHash = Write-Checksum $fullPackage
$fullSize = [long](Get-Item -LiteralPath $fullPackage).Length

$packageMetadata = [System.Collections.Generic.List[object]]::new()
$githubBaseUrl = "https://github.com/$GitHubRepository/releases/download/$tag"
$packageMetadata.Add([ordered]@{kind='Full';fileName=$fullPackageName;sha256=$fullHash;size=$fullSize;downloadUrl="$githubBaseUrl/$fullPackageName";downloadMirrors=@();baseVersion=$null;baseInstallManifestSha256=$null})

$previous = Get-ChildItem -LiteralPath $BaselineDirectory -File -Filter 'install-manifest.json' -Recurse -ErrorAction SilentlyContinue |
    ForEach-Object {
        try { $candidateManifest = Read-InstallManifest $_.FullName } catch { return }
        if ($candidateManifest.productId -eq 'NetBootDhcpTool' -and $candidateManifest.version -match '^\d+\.\d+\.\d+$') {
            $candidateVersion = [version]$candidateManifest.version
            if ($candidateVersion -lt [version]$version -and -not $_.FullName.StartsWith($releaseRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                [pscustomobject]@{ Version=$candidateVersion;ManifestPath=$_.FullName }
            }
        }
    } | Sort-Object Version -Descending | Select-Object -First 1
if ($null -ne $previous) {
    try {
        $baseManifest = Read-InstallManifest $previous.ManifestPath
        if ($baseManifest.productId -ne 'NetBootDhcpTool' -or $baseManifest.version -ne $previous.Version.ToString(3)) { throw 'Prior install manifest identity does not match its version.' }
        $baseManifestHash = (Get-FileHash -LiteralPath $previous.ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $baseRecords = @{}
        foreach ($file in $baseManifest.files) { $baseRecords[[string]$file.path] = $file }
        $targetRecords = @{}
        foreach ($file in $productFiles) { $targetRecords[$file.Path] = $file }
        $changed = @($productFiles | Where-Object { -not $baseRecords.ContainsKey($_.Path) -or [string]$baseRecords[$_.Path].sha256 -cne $_.Sha256 })
        $changed += $manifestFile
        $deleted = @($baseRecords.Keys | Where-Object { -not $targetRecords.ContainsKey($_) } | Sort-Object)
        $otaName = "NetBootDhcpTool-ota-v$($previous.Version.ToString(3))-to-v$version.zip"
        $otaPath = Join-Path $releaseRoot $otaName
        $otaEntries = @($changed | ForEach-Object {
            $baseHash = if ($baseRecords.ContainsKey($_.Path)) { [string]$baseRecords[$_.Path].sha256 } else { $null }
            [ordered]@{path=$_.Path;sha256=$_.Sha256;size=$_.Size;baseSha256=$baseHash}
        })
        $otaDocument = [ordered]@{schemaVersion=1;productId='NetBootDhcpTool';kind='Ota';targetVersion=$version;baseVersion=$previous.Version.ToString(3);baseInstallManifestSha256=$baseManifestHash;files=$otaEntries;deletedFiles=$deleted}
        New-PackageZip -Path $otaPath -PackageDocument $otaDocument -Files $changed
        $otaSize = [long](Get-Item -LiteralPath $otaPath).Length
        if ($otaSize -lt $fullSize) {
            $otaHash = Write-Checksum $otaPath
            $packageMetadata.Add([ordered]@{kind='Ota';fileName=$otaName;sha256=$otaHash;size=$otaSize;downloadUrl="$githubBaseUrl/$otaName";downloadMirrors=@();baseVersion=$previous.Version.ToString(3);baseInstallManifestSha256=$baseManifestHash})
            Write-Host "OTA package created: $otaName ($otaSize bytes, base $($previous.Version.ToString(3)))."
        } else {
            Remove-Item -LiteralPath $otaPath -Force
            Remove-Item -LiteralPath "$otaPath.sha256" -Force -ErrorAction SilentlyContinue
            Write-Host "OTA package is not smaller than Full ($otaSize >= $fullSize); only Full is published."
        }
    } catch {
        throw "Could not create an OTA package from $($previous.Version): $($_.Exception.Message)"
    }
} else {
    Write-Host 'No prior release with a valid install manifest exists; this release is Full-only and establishes the OTA baseline.'
}

Copy-Item -LiteralPath $product -Destination $versionedRelease -Recurse -Force
Push-Location $releaseRoot
try {
    & $sevenZip a -t7z $archive 'NetBootDhcpTool' -mx=9 -m0=lzma2 -ms=on -mmt=on
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $sevenZip t $archive
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
$archiveHash = Write-Checksum $archive

$releaseNotes = ''
$changeItems = @()
$releaseSection = $null
if (Test-Path -LiteralPath $releaseNotesPath) {
    $releaseNotesDocument = Get-Content -LiteralPath $releaseNotesPath -Raw
    $releaseSection = [regex]::Match($releaseNotesDocument, "(?ms)^##\s+v$([regex]::Escape($version))\b.*?(?=^##\s+|\z)")
}
if (-not $releaseSection.Success -and (Test-Path -LiteralPath $changeLogPath)) {
    $changeLog = Get-Content -LiteralPath $changeLogPath -Raw
    $releaseSection = [regex]::Match($changeLog, "(?ms)^##\s+v$([regex]::Escape($version))\b.*?(?=^##\s+|\z)")
    if (-not $releaseSection.Success) { $releaseSection = [regex]::Match($changeLog, '(?ms)^##\s+Unreleased\b.*?(?=^##\s+|\z)') }
}
if ($null -ne $releaseSection -and $releaseSection.Success) {
    $releaseNotes = $releaseSection.Value.Trim()
    $changeItems = @([regex]::Matches($releaseNotes, '(?m)^-\s+(?:Concrete change|User impact|变更内容|用户影响)[：:]?\s*(.+)$') | ForEach-Object { $_.Groups[1].Value.Trim() })
    if ($changeItems.Count -eq 0) { $changeItems = @([regex]::Matches($releaseNotes, '(?m)^-\s+(.+)$') | ForEach-Object { $_.Groups[1].Value.Trim() }) }
}
$manifestObject = [ordered]@{
    version = $version
    releasedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    archiveName = [System.IO.Path]::GetFileName($archive)
    archiveSha256 = $archiveHash
    downloadUrl = "$githubBaseUrl/$(Split-Path -Leaf $archive)"
    downloadMirrors = @()
    releasePageUrl = "https://github.com/$GitHubRepository/releases/tag/$tag"
    minimumSupportedVersion = '1.0.6'
    releaseNotes = $releaseNotes
    changes = $changeItems
    packages = @($packageMetadata)
}
[System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifestObject -Depth 12), [System.Text.UTF8Encoding]::new($false))
Write-Host "Output directory: $releaseRoot"
Write-Host "Portable folder: $product (move this folder without renaming it)."
Write-Host "Versioned install inventory: $versionedRelease"
Write-Host "Tools: $toolsRelease"
Write-Host "Legacy archive: $archive"
Write-Host "Full update package: $fullPackage"
Write-Host "Manifest: $manifestPath (formal publishing signs the final dual-source bytes)."
