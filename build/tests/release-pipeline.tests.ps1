$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $repoRoot 'build\release-pipeline\ReleaseState.psm1') -Force

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if ($Expected -is [array] -or $Actual -is [array]) {
        if (($Expected -join "`n") -cne ($Actual -join "`n")) { throw "$Message. Expected '$($Expected -join ', ')', got '$($Actual -join ', ')'" }
        return
    }
    if ($Expected -cne $Actual) { throw "$Message. Expected '$Expected', got '$Actual'." }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Message)
    try { & $Action } catch { return }
    throw $Message
}

$tempRoot = Join-Path $env:TEMP ("netboot-release-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    Assert-Equal '1.2.3' (Get-ReleaseVersionFromTag 'v1.2.3') 'Stable release tag parsing failed'
    Assert-Throws { Get-ReleaseVersionFromTag 'v1.2.3-rc.1' } 'Prerelease tags must not enter the stable release workflow.'
    Assert-Equal 'https://gitee.com/releases/file.7z' (Assert-SafeGiteeDownloadUrl 'https://gitee.com/releases/file.7z') 'Gitee HTTPS download URL validation failed'
    Assert-Throws { Assert-SafeGiteeDownloadUrl 'http://gitee.com/releases/file.7z' } 'Gitee download URLs must use HTTPS.'
    Assert-Throws { Assert-SafeGiteeDownloadUrl 'https://example.com/file.7z' } 'Non-Gitee download hosts must be rejected.'
    Assert-Throws { Assert-SafeGiteeDownloadUrl 'https://gitee.com/releases/file.7z?access_token=secret' } 'Download URLs must not embed API credentials.'

    $tag = 'v1.2.3'
    $commit = '0123456789abcdef0123456789abcdef01234567'
    $archiveName = Get-ReleaseArchiveName $tag
    $bundleDirectory = Join-Path $tempRoot 'bundle'
    New-Item -ItemType Directory -Path $bundleDirectory | Out-Null
    $archivePath = Join-Path $bundleDirectory $archiveName
    [System.IO.File]::WriteAllBytes($archivePath, [System.Text.Encoding]::UTF8.GetBytes('release archive bytes'))
    $archiveHash = Get-ReleaseFileSha256 $archivePath
    [System.IO.File]::WriteAllText("$archivePath.sha256", "$archiveHash  $archiveName", [System.Text.Encoding]::ASCII)
    $baseManifest = [ordered]@{
        version = '1.2.3'
        releasedAt = '2026-09-26T00:00:00Z'
        archiveName = $archiveName
        archiveSha256 = $archiveHash
        downloadUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/$tag/$archiveName"
        releasePageUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/tag/$tag"
        minimumSupportedVersion = '1.0.6'
        releaseNotes = 'Test notes'
        changes = @('Test change')
    }
    $baseManifestPath = Join-Path $bundleDirectory 'latest.json'
    [System.IO.File]::WriteAllText($baseManifestPath, (ConvertTo-Json -InputObject $baseManifest -Depth 6), [System.Text.UTF8Encoding]::new($false))

    $bundle = Get-ReleaseBundle -Directory $bundleDirectory -Tag $tag
    Assert-Equal $archiveHash $bundle.ArchiveSha256 'Local bundle SHA-256 was not accepted'
    Assert-Equal ([long](Get-Item -LiteralPath $archivePath).Length) $bundle.ArchiveSize 'Local bundle size was not recorded'

    $statePath = Join-Path $bundleDirectory 'release-state.json'
    $state = New-ReleaseState -Tag $tag -SourceCommit $commit -ArchiveSha256 $archiveHash
    Assert-ReleaseCachePolicy -CachePresent $false -GitHubReleaseExists $false -GiteeReleaseExists $false -Tag $tag
    Assert-ReleaseCachePolicy -CachePresent $true -GitHubReleaseExists $true -GiteeReleaseExists $true -Tag $tag
    Assert-Throws { Assert-ReleaseCachePolicy -CachePresent $false -GitHubReleaseExists $true -GiteeReleaseExists $false -Tag $tag } 'A missing cache must not rebuild when GitHub already has the Release.'
    Assert-Throws { Assert-ReleaseCachePolicy -CachePresent $false -GitHubReleaseExists $false -GiteeReleaseExists $true -Tag $tag } 'A missing cache must not rebuild when Gitee already has the Release.'
    Save-GiteeAttachmentCheckpoint -State $state -Name $archiveName -AttachmentId '12345' -DownloadUrl 'https://gitee.com/download/archive' -Status 'verified'
    Save-ReleaseState -Path $statePath -State $state
    $state.giteeReleaseId = 42
    Save-ReleaseState -Path $statePath -State $state
    $reopened = Read-ReleaseState -Path $statePath -Tag $tag -SourceCommit $commit -ArchiveSha256 $archiveHash
    Assert-Equal 42 $reopened.giteeReleaseId 'Release state could not be atomically replaced after a checkpoint'
    Assert-Equal '12345' $reopened.giteeAssets[$archiveName].attachmentId 'Gitee attachment ID did not survive a cache readback'
    Assert-Equal 'verified' $reopened.giteeAssets[$archiveName].status 'Gitee attachment status did not survive a cache readback'
    Assert-Throws { Read-ReleaseState -Path $statePath -Tag $tag -SourceCommit ('f' * 40) -ArchiveSha256 $archiveHash } 'A release cache from another source commit must fail closed.'

    $dualManifestPath = Join-Path $bundleDirectory 'final-latest.json'
    $dualManifest = New-DualSourceManifest -BaseManifestPath $baseManifestPath -Tag $tag -GitHubRepository 'shashouaq/NetBootDhcpTool' -GiteeReleasePageUrl "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/$tag" -GiteeArchiveDownloadUrl 'https://gitee.com/download/archive' -OutputPath $dualManifestPath
    Assert-Equal 'https://gitee.com/download/archive' $dualManifest.downloadUrl 'Gitee must be the manifest primary source'
    Assert-Equal @("https://github.com/shashouaq/NetBootDhcpTool/releases/download/$tag/$archiveName") @($dualManifest.downloadMirrors) 'GitHub mirror URL was not preserved'
    Assert-Equal $archiveHash $dualManifest.archiveSha256 'Dual-source manifest changed the archive checksum'
    Assert-Equal "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/$tag" $dualManifest.releasePageUrl 'Gitee release page link was not set'

    $tamperedArchive = [System.IO.File]::ReadAllBytes($archivePath)
    [System.IO.File]::WriteAllBytes($archivePath, [System.Text.Encoding]::UTF8.GetBytes('different archive bytes'))
    Assert-Throws { Get-ReleaseBundle -Directory $bundleDirectory -Tag $tag } 'A package that differs from its sidecar must be rejected.'
    [System.IO.File]::WriteAllBytes($archivePath, $tamperedArchive)

    $formalWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\formal-release.yml')
    $windowsWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\windows-ci.yml')
    if ($formalWorkflow -notmatch '(?m)^  workflow_dispatch:') { throw 'Formal Release must be explicitly dispatched.' }
    if ($formalWorkflow -match '(?m)^  release:') { throw 'Formal Release must not automatically start after an asset has already been published.' }
    if ($formalWorkflow -notmatch '(?ms)  verify-ci:.*?runs-on: windows-latest') { throw 'Exact-commit validation must remain on a GitHub-hosted runner.' }
    if ($formalWorkflow -notmatch '(?ms)  publish:.*?runs-on: \[self-hosted, windows, x64, netboot-release\]') { throw 'Release publication must use the dedicated Windows x64 release runner.' }
    if ($formalWorkflow -notmatch 'git cat-file -e "\$\{releaseCommit\}:\$requiredTagFile"') { throw 'The CI gate must reject legacy tags that do not contain the publisher used by the release job.' }
    $sdkPathSetup = $formalWorkflow.IndexOf('DOTNET_INSTALL_DIR=$sdkRoot', [System.StringComparison]::Ordinal)
    $dotnetAction = $formalWorkflow.IndexOf('uses: actions/setup-dotnet@v5', [System.StringComparison]::Ordinal)
    if ($sdkPathSetup -lt 0 -or $dotnetAction -lt $sdkPathSetup -or
        $formalWorkflow -notmatch "LOCALAPPDATA 'NetBootDhcpTool\\dotnet'") {
        throw 'The self-hosted runner must install the pinned SDK into its persistent user-writable directory before setup-dotnet runs.'
    }
    if ($formalWorkflow -notmatch 'build[\\/]publish-release\.ps1') { throw 'Formal Release must invoke the idempotent local publisher.' }
    if ($windowsWorkflow -notmatch '(?m)^    runs-on: windows-latest') { throw 'Day-to-day CI must remain on GitHub-hosted Windows.' }
    $publisher = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'build\publish-release.ps1')
    if ($publisher -notmatch "'--draft'") { throw 'GitHub Release must remain draft until final verification.' }
    if ($publisher -notmatch "prerelease = 'true'") { throw 'Gitee Release must remain prerelease until final verification.' }
    $archivesReady = $publisher.IndexOf('Ensure-GiteeAsset -LocalPath $bundle.ArchivePath', [System.StringComparison]::Ordinal)
    $manifestPublished = $publisher.IndexOf("Ensure-GiteeAsset -LocalPath `$finalManifestPath", [System.StringComparison]::Ordinal)
    $releasePromoted = $publisher.IndexOf('$giteeRelease = Publish-GiteeRelease', [System.StringComparison]::Ordinal)
    if ($archivesReady -lt 0 -or $manifestPublished -lt $archivesReady -or $releasePromoted -lt $manifestPublished) {
        throw 'Stable Release promotion must follow archive and latest.json remote verification.'
    }
    $archiveOperations = @(
        'Ensure-GitHubAsset -LocalPath $bundle.ArchivePath',
        'Ensure-GitHubAsset -LocalPath $bundle.ChecksumPath',
        'Ensure-GiteeAsset -LocalPath $bundle.ChecksumPath',
        'Ensure-GiteeAsset -LocalPath $bundle.ArchivePath'
    )
    foreach ($operation in $archiveOperations) {
        $position = $publisher.IndexOf($operation, [System.StringComparison]::Ordinal)
        if ($position -lt 0 -or $position -gt $manifestPublished) {
            throw "Both remote archive and sidecar verification must precede latest.json publication: $operation"
        }
    }
    $githubManifestUpload = $publisher.IndexOf('Ensure-GitHubAsset -LocalPath $finalManifestPath', [System.StringComparison]::Ordinal)
    $githubManifestReadback = $publisher.IndexOf('$githubManifestPath = Download-GitHubAsset ''latest.json''', [System.StringComparison]::Ordinal)
    $githubPromotion = $publisher.IndexOf('$githubRelease = Publish-GitHubRelease', [System.StringComparison]::Ordinal)
    $giteePromotion = $publisher.IndexOf('$giteeRelease = Publish-GiteeRelease', [System.StringComparison]::Ordinal)
    if ($githubManifestUpload -lt $manifestPublished -or
        $githubManifestReadback -lt $githubManifestUpload -or
        $githubPromotion -lt $githubManifestReadback -or
        $giteePromotion -lt $githubPromotion) {
        throw 'Both latest.json files must be verified before publication, with Gitee promoted last.'
    }
    if ($publisher -match '(?m)\$configPath = Join-Path \$stateDirectory' -or
        $publisher -notmatch '\$curlConfig \| & \$curl --config -') {
        throw 'The Gitee access token must be streamed to curl and never saved in persistent release state.'
    }
    $cacheGuard = $publisher.IndexOf('Assert-ReleaseCachePolicy', [System.StringComparison]::Ordinal)
    $packageBuild = $publisher.IndexOf('[Package] Building the single release archive', [System.StringComparison]::Ordinal)
    if ($cacheGuard -lt 0 -or $packageBuild -lt $cacheGuard) {
        throw 'An existing remote Release without its persistent cache must fail before any rebuild.'
    }
    if (Test-Path -LiteralPath (Join-Path $repoRoot '.github\workflows\gitee-release-sync.yml')) { throw 'The old GitHub-hosted large-file sync workflow must be removed.' }
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'build\sync-gitee-release.ps1')) { throw 'The old GitHub-download-to-Gitee publisher must be removed.' }

    Write-Output 'RELEASE_PIPELINE_TESTS_OK'
} finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $resolvedTempBase = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if ($resolvedTemp.StartsWith("$resolvedTempBase$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp).StartsWith('netboot-release-tests-', [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
