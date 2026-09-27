$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot "stop-test-processes.ps1")
$buildDirectories = Get-ChildItem -LiteralPath $root -Recurse -Directory -Force |
    Where-Object { $_.Name -in @("bin", "obj") } |
    Sort-Object FullName -Descending
foreach ($directory in $buildDirectories) {
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}

$release = Join-Path $root "release"
New-Item -ItemType Directory -Force -Path $release | Out-Null
$versionPattern = '^NetBootDhcpTool-v(?<version>\d+\.\d+\.\d+)(?:\.7z(?:\.sha256)?|)$'
$versionEntries = @(
    Get-ChildItem -LiteralPath $release -Force |
        ForEach-Object {
            if ($_.Name -match $versionPattern) {
                [pscustomobject]@{ Name = $_.Name; Version = [version]$Matches.version }
            }
        }
)
$keepVersions = @(
    $versionEntries |
        Sort-Object Version -Descending |
        Select-Object -ExpandProperty Version -Unique |
        Select-Object -First 2
)
if ($versionEntries.Count -eq 0) {
    Write-Host "No recognized versioned release assets found; release assets left untouched."
    exit 0
}
$keepNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @("NetBootDhcpTool", "NetBootDhcpTool-tools", "latest.json", "latest.json.sig", "README_RUN.txt")) {
    [void]$keepNames.Add($name)
}
$localBuilds = @(
    Get-ChildItem -LiteralPath $release -Directory -Filter 'local-build-*' -Force |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 2
)
foreach ($localBuild in $localBuilds) { [void]$keepNames.Add($localBuild.Name) }
foreach ($version in $keepVersions) {
    [void]$keepNames.Add("NetBootDhcpTool-v$version")
    [void]$keepNames.Add("NetBootDhcpTool-v$version.7z")
    [void]$keepNames.Add("NetBootDhcpTool-v$version.7z.sha256")
    [void]$keepNames.Add("NetBootDhcpTool-full-v$version.zip")
    [void]$keepNames.Add("NetBootDhcpTool-full-v$version.zip.sha256")
}
$keepVersionTexts = @($keepVersions | ForEach-Object { $_.ToString(3) })
Get-ChildItem -LiteralPath $release -File -Force |
    ForEach-Object {
        if ($_.Name -match '^NetBootDhcpTool-ota-v\d+\.\d+\.\d+-to-v(?<version>\d+\.\d+\.\d+)\.zip(?:\.sha256)?$' -and $Matches.version -in $keepVersionTexts) {
            [void]$keepNames.Add($_.Name)
        }
    }
Get-ChildItem -LiteralPath $release -Force |
    Where-Object { -not $keepNames.Contains($_.Name) } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
Write-Host "Kept release versions: $($keepVersions -join ', ')"
Write-Host "Kept local package builds: $($localBuilds.Name -join ', ')"
Write-Host "Removed stale build caches and release assets older than the two newest versions."
