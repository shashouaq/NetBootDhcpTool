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
    $otherCommit = '89abcdef0123456789abcdef0123456789abcdef'
    $passingRun = [pscustomobject]@{ head_sha = $commit; event = 'push'; head_branch = 'main'; conclusion = 'success' }
    Assert-Equal $true (Test-SuccessfulMainWindowsCiRun -Runs @($passingRun) -Commit $commit) 'A successful main push must satisfy the exact-commit CI gate'
    Assert-Equal $false (Test-SuccessfulMainWindowsCiRun -Runs @($passingRun) -Commit $otherCommit) 'CI for the release tag must not authorize a different publisher commit'
    foreach ($wrongRun in @(
        [pscustomobject]@{ head_sha = $commit; event = 'pull_request'; head_branch = 'main'; conclusion = 'success' },
        [pscustomobject]@{ head_sha = $commit; event = 'push'; head_branch = 'feature'; conclusion = 'success' },
        [pscustomobject]@{ head_sha = $commit; event = 'push'; head_branch = 'main'; conclusion = 'failure' }
    )) {
        Assert-Equal $false (Test-SuccessfulMainWindowsCiRun -Runs @($wrongRun) -Commit $commit) 'Only a successful push CI run for the exact main commit may authorize publication'
    }

    $releaseBody = "中文 notes`nEnglish notes"
    $createPayload = New-GiteeReleasePayload -Operation Create -Tag $tag -Name "NetBoot DHCP Tool $tag" -Body $releaseBody -TargetCommit $commit
    $createJson = ConvertTo-Json -InputObject $createPayload -Depth 8 -Compress | ConvertFrom-Json -AsHashtable
    Assert-Equal @('body', 'name', 'prerelease', 'tag_name', 'target_commitish') @($createJson.Keys | Sort-Object) 'Gitee create must serialize every required Release field'
    Assert-Equal $releaseBody $createJson.body 'Gitee Release notes must survive JSON serialization'
    Assert-Equal $commit $createJson.target_commitish 'Gitee create must target the checked main commit'
    Assert-Equal $true $createJson.prerelease 'Gitee create must stay prerelease until verification'
    Assert-Throws { New-GiteeReleasePayload -Operation Create -Tag $tag -Name 'test' -Body '' -TargetCommit 'bad-sha' } 'Gitee create must reject an invalid target commit'
    $updatePayload = New-GiteeReleasePayload -Operation Update -Tag $tag -Name "NetBoot DHCP Tool $tag" -Body $releaseBody
    if ($updatePayload.Contains('prerelease')) { throw 'Updating Gitee Release notes must not change publication state.' }
    $publishPayload = New-GiteeReleasePayload -Operation Publish -Tag $tag -Name "NetBoot DHCP Tool $tag" -Body $releaseBody
    Assert-Equal $false $publishPayload.prerelease 'Only the final Gitee update may promote a stable Release'

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
    if ($formalWorkflow -notmatch 'publisher_commit=\$publisherCommit' -or
        $formalWorkflow -notmatch 'Test-SuccessfulMainWindowsCiRun' -or
        $formalWorkflow -notmatch '@\(\$releaseCommit, \$publisherCommit\)' -or
        $formalWorkflow -notmatch 'PUBLISHER_COMMIT: \$\{\{ needs\.verify-ci\.outputs\.publisher_commit \}\}') {
        throw 'The current publisher commit and the release tag commit must each have a successful exact-main push CI run before publication.'
    }
    $workflowLf = $formalWorkflow.Replace("`r`n", "`n")
    foreach ($workflowText in @($workflowLf, $workflowLf.Replace("`n", "`r`n"))) {
        $publishTimeout = [regex]::Match($workflowText, '(?m)^    timeout-minutes: (\d+)\r?$')
        if (-not $publishTimeout.Success -or [int]$publishTimeout.Groups[1].Value -gt 30) {
            throw 'The self-hosted publish job must fail within 30 minutes if a platform or credential operation hangs.'
        }
    }
    $publisherSourceSetup = $formalWorkflow.IndexOf('Load release tooling from the workflow commit', [System.StringComparison]::Ordinal)
    $publisherInvocation = $formalWorkflow.IndexOf('.\build\publish-release.ps1', [System.StringComparison]::Ordinal)
    if ($publisherSourceSetup -lt 0 -or $publisherInvocation -lt $publisherSourceSetup -or
        $formalWorkflow -notmatch 'PUBLISHER_COMMIT: \$\{\{ needs\.verify-ci\.outputs\.publisher_commit \}\}' -or
        $formalWorkflow -notmatch 'application sources remain at the requested release tag') {
        throw 'Release coordination must use CI-verified tooling while building and publishing the requested tag source.'
    }
    foreach ($toolPath in @('build/publish-release.ps1', 'build/release-pipeline/ReleaseState.psm1', 'build/publish.ps1')) {
        if (-not $formalWorkflow.Contains($toolPath)) {
            throw "The release job must load $toolPath from its CI-verified workflow commit."
        }
    }
    $sdkPathSetup = $formalWorkflow.IndexOf('DOTNET_INSTALL_DIR=$sdkRoot', [System.StringComparison]::Ordinal)
    $dotnetAction = $formalWorkflow.IndexOf('uses: actions/setup-dotnet@v5', [System.StringComparison]::Ordinal)
    if ($sdkPathSetup -lt 0 -or $dotnetAction -lt $sdkPathSetup -or
        $formalWorkflow -notmatch "LOCALAPPDATA 'NetBootDhcpTool\\dotnet'") {
        throw 'The self-hosted runner must install the pinned SDK into its persistent user-writable directory before setup-dotnet runs.'
    }
    if ($formalWorkflow -notmatch 'build[\\/]publish-release\.ps1') { throw 'Formal Release must invoke the idempotent local publisher.' }
    foreach ($credentialSetting in @(
        "GIT_CONFIG_COUNT: '1'",
        'GIT_CONFIG_KEY_0: credential.helper',
        "GIT_CONFIG_VALUE_0: ''",
        "GIT_TERMINAL_PROMPT: '0'",
        'GCM_INTERACTIVE: never'
    )) {
        if (-not $formalWorkflow.Contains($credentialSetting)) {
            throw "The release publisher must bypass machine-wide interactive Git credential helpers: missing $credentialSetting."
        }
    }
    if ($windowsWorkflow -notmatch '(?m)^    runs-on: windows-latest') { throw 'Day-to-day CI must remain on GitHub-hosted Windows.' }
    $publisher = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'build\publish-release.ps1')
    if ($publisher -notmatch '(?s)function Invoke-GiteeJsonRequest.*?-ContentType ''application/json; charset=utf-8''.*?-Body \$jsonBody' -or
        $publisher -notmatch 'Invoke-GiteeJsonRequest -Uri .*?/releases" -Method Post') {
        throw 'Gitee Release create/update calls must send a JSON request body so the API receives its required fields.'
    }
    if ($publisher -notmatch 'function Assert-ReleaseAssetFileName' -or
        $publisher -notmatch 'Join-Path \$finalManifestDirectory ''latest\.json''') {
        throw 'Every remote asset must be uploaded using the exact filename clients request, including latest.json.'
    }
    if ($publisher -notmatch "'--draft'") { throw 'GitHub Release must remain draft until final verification.' }
    foreach ($operation in @('Create', 'Update', 'Publish')) {
        if ($publisher -notmatch "New-GiteeReleasePayload -Operation $operation") {
            throw "Gitee Release $operation must use the checked JSON field contract."
        }
    }
    $archivesReady = $publisher.IndexOf('Ensure-GiteeAsset -LocalPath $bundle.ArchivePath', [System.StringComparison]::Ordinal)
    $manifestPublished = $publisher.LastIndexOf("Ensure-GiteeAsset -LocalPath `$finalManifestPath", [System.StringComparison]::Ordinal)
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
    $githubManifestUpload = $publisher.LastIndexOf('Ensure-GitHubAsset -LocalPath $finalManifestPath', [System.StringComparison]::Ordinal)
    $githubManifestReadback = $publisher.IndexOf('$script:verifiedAssets["$mirror/latest.json"]', [System.StringComparison]::Ordinal)
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

    & (Join-Path $PSScriptRoot 'release-resilience.tests.ps1')

    Write-Output 'RELEASE_PIPELINE_TESTS_OK'
} finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $resolvedTempBase = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if ($resolvedTemp.StartsWith("$resolvedTempBase$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp).StartsWith('netboot-release-tests-', [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
