param(
    [string]$GitHubRepository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'shashouaq/NetBootDhcpTool' }),
    [string]$OutputDirectory,
    [string]$BaselineDirectory
)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'package-content.ps1')
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
. (Join-Path $PSScriptRoot 'release-identity.ps1')
$version = (Get-NetBootVersionMetadata -RepositoryRoot $root).Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version not found or invalid in $projectFile" }
$tag = "v$version"
$versionedRelease = Join-Path $releaseRoot "NetBootDhcpTool-v$version"
$archive = Join-Path $releaseRoot "NetBootDhcpTool-v$version.7z"
$fullPackageName = "NetBootDhcpTool-full-v$version.7z"
$fullPackage = Join-Path $releaseRoot $fullPackageName
$manifestPath = Join-Path $releaseRoot 'latest-v2.json'
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
    return Get-ProductPackageContentManifest -Directory $Directory
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
foreach ($publishRoot in @($product, $toolsRelease)) {
    Get-ChildItem -LiteralPath $publishRoot -File -Recurse -Filter '*.pdb' | Remove-Item -Force
}
foreach ($directory in @('config', 'i18n', 'assets')) { Copy-Item -LiteralPath (Join-Path $root $directory) -Destination $product -Recurse -Force }
Copy-ProductDocumentation -SourceRoot $root -DestinationProduct $product
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
$packageWork = Join-Path $releaseRoot '.full7z-package'
New-Item -ItemType Directory -Path (Join-Path $packageWork 'payload') -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $packageWork 'update-package.json'), (ConvertTo-Json -InputObject $fullDocument -Depth 12), [System.Text.UTF8Encoding]::new($false))
foreach ($file in $fullFiles) {
    $destination = Join-Path (Join-Path $packageWork 'payload') ([string]$file.Path -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.SourcePath -Destination $destination
}
Push-Location $packageWork
try {
    & $sevenZip a -t7z $fullPackage 'update-package.json' 'payload' -mx=9 -m0=lzma2 -ms=on -mmt=on
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $sevenZip t $fullPackage
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
Remove-OwnedOutput $packageWork
$fullHash = Write-Checksum $fullPackage
$fullSize = [long](Get-Item -LiteralPath $fullPackage).Length

$sevenZipPackageMetadata = [System.Collections.Generic.List[object]]::new()
$githubBaseUrl = "https://github.com/$GitHubRepository/releases/download/$tag"
$sevenZipPackageMetadata.Add([ordered]@{kind='Full';fileName=$fullPackageName;sha256=$fullHash;size=$fullSize;downloadUrl="$githubBaseUrl/$fullPackageName";downloadMirrors=@();baseVersion=$null;baseInstallManifestSha256=$null})
Write-Host "Full 7z update payload created: $fullPackageName ($fullSize bytes). OTA remains disabled until the full-install migration closes."

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
    packages = @()
    sevenZipPackages = @($sevenZipPackageMetadata)
}
[System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifestObject -Depth 12), [System.Text.UTF8Encoding]::new($false))
Write-Host "Output directory: $releaseRoot"
Write-Host "Portable folder: $product (move this folder without renaming it)."
Write-Host "Versioned install inventory: $versionedRelease"
Write-Host "Tools: $toolsRelease"
Write-Host "Legacy archive: $archive"
Write-Host "Full 7z update package: $fullPackage"
Write-Host "Manifest: $manifestPath (formal publishing signs the final dual-source bytes)."
