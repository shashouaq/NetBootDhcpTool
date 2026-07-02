param(
    [string]$GitHubRepository = $env:GITHUB_REPOSITORY
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $root "src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj"
[xml]$projectXml = Get-Content $projectFile
$version = $projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version not found in $projectFile"
}
$release = Join-Path $root "release\NetBootDhcpTool"
$versionedRelease = Join-Path $root "release\NetBootDhcpTool-v$version"
$toolsRelease = Join-Path $root "release\NetBootDhcpTool-tools"
$archive = Join-Path $root "release\NetBootDhcpTool-v$version.7z"
$checksumFile = "$archive.sha256"
$manifest = Join-Path $root "release\latest.json"
Set-Location $root
& (Join-Path $PSScriptRoot "stop-test-processes.ps1")
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
if (Test-Path $versionedRelease) { Remove-Item $versionedRelease -Recurse -Force }
if (Test-Path $toolsRelease) { Remove-Item $toolsRelease -Recurse -Force }
if (Test-Path $archive) { Remove-Item $archive -Force }
if (Test-Path $checksumFile) { Remove-Item $checksumFile -Force }
if (Test-Path $manifest) { Remove-Item $manifest -Force }
$dotnet = & (Join-Path $PSScriptRoot "resolve-dotnet.ps1")
& $dotnet restore .\src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj -r win-x64 --configfile .\NuGet.config --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet restore .\src\NetBootDhcpTool.DhcpVerifier\NetBootDhcpTool.DhcpVerifier.csproj -r win-x64 --configfile .\NuGet.config --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish .\src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish .\src\NetBootDhcpTool.DhcpVerifier\NetBootDhcpTool.DhcpVerifier.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $toolsRelease
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item .\config $release -Recurse -Force
Copy-Item .\i18n $release -Recurse -Force
Copy-Item .\assets $release -Recurse -Force
Copy-Item .\docs $release -Recurse -Force
Copy-Item .\README.md $release -Force
Copy-Item .\PROJECT_MEMORY.md $release -Force
Set-Content -Path (Join-Path (Split-Path $release -Parent) "README_RUN.txt") -Value "Run NetBootDhcpTool\NetBootDhcpTool.exe for the GUI application.`r`nNetBootDhcpTool-tools contains command-line diagnostic tools only." -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $release "logs") | Out-Null
Copy-Item $release $versionedRelease -Recurse -Force
$sevenZip = "C:\Program Files\7-Zip\7z.exe"
if (-not (Test-Path $sevenZip)) { throw "7-Zip not found: $sevenZip" }
& $sevenZip a -t7z $archive (Join-Path $versionedRelease "*") -mx=9 -m0=lzma2 -ms=on -mmt=on
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $sevenZip t $archive
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$hash = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksumFile -Value "$hash  $(Split-Path $archive -Leaf)" -Encoding ASCII
$downloadUrl = $null
if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) {
    $downloadUrl = "https://github.com/$GitHubRepository/releases/download/v$version/$(Split-Path $archive -Leaf)"
}
$manifestObject = [ordered]@{
    version = $version
    releasedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    archiveName = (Split-Path $archive -Leaf)
    archiveSha256 = $hash
    downloadUrl = $downloadUrl
    releasePageUrl = if ([string]::IsNullOrWhiteSpace($GitHubRepository)) { $null } else { "https://github.com/$GitHubRepository/releases/tag/v$version" }
    minimumSupportedVersion = "1.0.6"
}
$manifestObject | ConvertTo-Json -Depth 3 | Set-Content -Path $manifest -Encoding UTF8
Write-Host "Published: $release"
Write-Host "Versioned: $versionedRelease"
Write-Host "Tools: $toolsRelease"
Write-Host "Archive: $archive"
Write-Host "Checksum: $checksumFile"
Write-Host "Manifest: $manifest"
