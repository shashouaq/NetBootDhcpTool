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
    . (Join-Path $repoRoot 'build\release-identity.ps1')
    Assert-Equal '1.1.0' (Get-NetBootReleaseIdentity -Tag 'v1.1.0-rc.1' -ReleaseCandidate).Version 'RC binary version remains numeric'
    Assert-Equal $true (Get-NetBootReleaseIdentity -Tag 'v1.1.0-rc.1' -ReleaseCandidate).IsCandidate 'RC mode is explicit'
    Assert-Throws { Get-NetBootReleaseIdentity -Tag 'v1.1.0' -ReleaseCandidate } 'Candidate mode must reject a stable tag'
    Assert-Throws { Get-NetBootReleaseIdentity -Tag 'v1.1.0-rc.1' } 'Formal mode must reject RC tags'
    Assert-Throws { Get-NetBootReleaseIdentity -Tag 'v1.1.0-rc.0' -ReleaseCandidate } 'RC ordinal must be positive'
    . (Join-Path $repoRoot 'build\legacy-manifest.ps1')
    $newRelease = Join-Path $tempRoot 'v2-release'
    New-Item -ItemType Directory -Path $newRelease | Out-Null
    [IO.File]::WriteAllText((Join-Path $newRelease 'latest-v2.json'), '{"version":"1.1.0"}')
    Copy-NetBootFrozenLegacyManifest -RepositoryRoot $repoRoot -Destination $newRelease
    Assert-Equal '10d2b102b615c007e81616b9abe15c939b43f212e9667910822b78975e9a48a7' (Get-ReleaseFileSha256 (Join-Path $newRelease 'latest.json')) 'Publishing V2 must preserve the frozen v1.0.20 bytes'
    Assert-Equal '1.1.0' ((Get-Content (Join-Path $newRelease 'latest-v2.json') -Raw | ConvertFrom-Json).version) 'Legacy publication must not rewrite the V2 manifest'
    [IO.File]::WriteAllText((Join-Path $newRelease 'latest.json'), '{"version":"1.1.0"}')
    Assert-Throws { Copy-NetBootFrozenLegacyManifest -RepositoryRoot $repoRoot -Destination $newRelease } 'A new manifest must never overwrite the legacy channel'
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
    $fullName = 'NetBootDhcpTool-full-v1.2.3.7z'
    $fullPath = Join-Path $bundleDirectory $fullName
    [System.IO.File]::WriteAllBytes($fullPath, [System.Text.Encoding]::UTF8.GetBytes('full package bytes'))
    $fullHash = Get-ReleaseFileSha256 $fullPath
    [System.IO.File]::WriteAllText("$fullPath.sha256", "$fullHash  $fullName", [System.Text.Encoding]::ASCII)
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
        packages = @()
        sevenZipPackages = @(@{
            kind = 'Full'
            fileName = $fullName
            sha256 = $fullHash
            size = [long](Get-Item -LiteralPath $fullPath).Length
            downloadUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/$tag/$fullName"
            downloadMirrors = @()
        })
    }
    $baseManifestPath = Join-Path $bundleDirectory 'latest-v2.json'
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

    $dualManifestPath = Join-Path $bundleDirectory 'final-latest-v2.json'
    $giteePackageUrl = 'https://gitee.com/joel20230302/NetBootDhcpTool/attach_files/12345/download'
    $dualManifest = New-DualSourceManifest -BaseManifestPath $baseManifestPath -Tag $tag -GitHubRepository 'shashouaq/NetBootDhcpTool' -GiteeReleasePageUrl "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/$tag" -GiteeArchiveDownloadUrl 'https://gitee.com/download/archive' -GiteeAssetUrls @{ $fullName=$giteePackageUrl } -OutputPath $dualManifestPath
    Assert-Equal 'https://gitee.com/download/archive' $dualManifest.downloadUrl 'Gitee must be the manifest primary source'
    Assert-Equal @("https://github.com/shashouaq/NetBootDhcpTool/releases/download/$tag/$archiveName") @($dualManifest.downloadMirrors) 'GitHub mirror URL was not preserved'
    Assert-Equal $archiveHash $dualManifest.archiveSha256 'Dual-source manifest changed the archive checksum'
    Assert-Equal "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/$tag" $dualManifest.releasePageUrl 'Gitee release page link was not set'
    Assert-Equal $giteePackageUrl $dualManifest.sevenZipPackages[0].downloadUrl 'Full 7z Gitee attachment URL was not included in the signed manifest'
    Assert-Equal @("https://github.com/shashouaq/NetBootDhcpTool/releases/download/$tag/$fullName") @($dualManifest.sevenZipPackages[0].downloadMirrors) 'Full 7z GitHub mirror was not included in the signed manifest'

    $tamperedArchive = [System.IO.File]::ReadAllBytes($archivePath)
    [System.IO.File]::WriteAllBytes($archivePath, [System.Text.Encoding]::UTF8.GetBytes('different archive bytes'))
    Assert-Throws { Get-ReleaseBundle -Directory $bundleDirectory -Tag $tag } 'A package that differs from its sidecar must be rejected.'
    [System.IO.File]::WriteAllBytes($archivePath, $tamperedArchive)

    $formalWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\formal-release.yml')
    $giteeWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\gitee-mirror.yml')
    $windowsWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\windows-ci.yml')
    if ($formalWorkflow -notmatch '(?m)^  workflow_dispatch:') { throw 'Formal Release must be explicitly dispatched.' }
    if ($formalWorkflow -match '(?m)^  release:') { throw 'Formal Release must not run after a Release has already been published.' }
    if ($formalWorkflow -match '(?i)self-hosted|netboot-release|GITEE_TOKEN') { throw 'Formal Release must not depend on the local Runner or Gitee credentials.' }
    foreach ($job in @('verify-ci', 'build-once', 'publish-github')) {
        $jobPattern = '(?ms)^  ' + [regex]::Escape($job) + ':\r?\n.*?runs-on: windows-latest'
        if ($formalWorkflow -notmatch $jobPattern) { throw "Formal Release job $job must use GitHub-hosted Windows." }
    }
    if ($formalWorkflow -notmatch 'Test-SuccessfulMainWindowsCiRun' -or
        -not $formalWorkflow.Contains('publisher_commit=$publisherCommit') -or
        -not $formalWorkflow.Contains('release_commit=$releaseCommit') -or
        $formalWorkflow -notmatch 'build/release-pipeline/ReleaseState.psm1') {
        throw 'The tag and workflow commits must each pass exact-commit Windows CI before release.'
    }
    if ($formalWorkflow -notmatch 'NETBOOT_UPDATE_SIGNING_PRIVATE_KEY: \$\{\{ secrets\.NETBOOT_UPDATE_SIGNING_PRIVATE_KEY \}\}' -or
        $formalWorkflow -notmatch 'formal-release-\$\{\{ inputs\.release_tag \}\}' -or
        $formalWorkflow -notmatch 'actions/upload-artifact@v7' -or
        $formalWorkflow -notmatch 'actions/download-artifact@v5') {
        throw 'The build job must sign and persist one package set for the separate GitHub publish job.'
    }
    if ($formalWorkflow.IndexOf('actions/upload-artifact@v7', [System.StringComparison]::Ordinal) -gt
        $formalWorkflow.IndexOf('actions/download-artifact@v5', [System.StringComparison]::Ordinal)) {
        throw 'GitHub publication must consume the artifact produced by the single build job.'
    }
    if ($formalWorkflow -notmatch 'scripts/Prepare-GitHubRelease.ps1' -or
        $formalWorkflow -notmatch 'scripts/Publish-GitHubRelease.ps1') {
        throw 'Formal Release must call the new hosted build and GitHub-only publisher scripts.'
    }
    if ($windowsWorkflow -notmatch '(?m)^    runs-on: windows-latest' -or
        $windowsWorkflow -notmatch 'build[\\/]tests[\\/]gitee-mirror\.tests\.ps1') {
        throw 'GitHub-hosted CI must run the isolated Gitee mirror recovery regression.'
    }

    $giteeScript = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts\Publish-GiteeMirror.ps1')
    $secureMirrorScript = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts\Invoke-GiteeMirrorSecure.ps1')
    foreach ($requiredSecureBehavior in @(
        'Read-Host',
        '-AsSecureString',
        'SecureStringToBSTR',
        '$env:GITEE_TOKEN = $tokenText',
        'Remove-Item Env:GITEE_TOKEN',
        'ZeroFreeBSTR',
        '& $publisherPath -Tag $Tag -AssetDirectory $AssetDirectory -TelemetryPath $telemetryPath'
    )) {
        if (-not $secureMirrorScript.Contains($requiredSecureBehavior)) { throw "Secure Gitee launcher is missing required credential handling: $requiredSecureBehavior." }
    }
    if ($secureMirrorScript -match '(?i)(?:param\s*\([^)]*\$Token|-[\w]+Token\s+\$tokenText|Write-(?:Host|Output|AllText)[^\r\n]*\$tokenText)') {
        throw 'Secure Gitee launcher must not accept or print a token parameter.'
    }
    $githubScript = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts\Publish-GitHubRelease.ps1')
    $candidateWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\release-candidate.yml')
    if (-not $githubScript.Contains('if (-not $ReleaseCandidate -and [bool]$release.prerelease)') -or
        -not $githubScript.Contains("make_latest = 'false'") -or
        $candidateWorkflow -notmatch 'Publish-GitHubRelease\.ps1.*-ReleaseCandidate -SourceCommit' -or
        $candidateWorkflow -match 'runs-on:.*self-hosted|GITEE_TOKEN') {
        throw 'RC publishing must stay prerelease, use exact source identity and never run a Gitee upload or self-hosted runner.'
    }
    if ($candidateWorkflow -notmatch 'SkipHttpErrorCheck' -or $candidateWorkflow -notmatch 'StatusCode -ne 404' -or
        $candidateWorkflow -notmatch 'needs: candidate' -or $candidateWorkflow -notmatch 'actions/download-artifact@v5') {
        throw 'RC absence must use explicit HTTP status; publication recovery must consume the original signed artifact.'
    }
    $prepareScript = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts\Prepare-GitHubRelease.ps1')
    $packageBuilder = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'build\publish.ps1')
    if ($packageBuilder -notmatch 'docs\\RELEASE_NOTES\.md' -or
        $packageBuilder -notmatch 'docs\\FEATURE_CHANGELOG\.md' -or
        $packageBuilder -notmatch 'releaseSection') {
        throw 'Package metadata must prefer the current bilingual release notes and retain the feature change-log fallback.'
    }
    foreach ($requiredMirrorBehavior in @(
        'api.github.com/repos/$Repository/releases/tags',
        'browser_download_url',
        'FormFields',
        'access_token = $Token',
        'AssetDirectory',
        'Get-GitHubAssetSha256',
        'Assert-GitHubAssetFile',
        'Download-VerifiedGitHubAsset',
        'Get-MirrorSha256',
        'Assert-MirrorManifestSignature',
        'Get-GiteeAttachmentsForMirror',
        'Get-GiteeAttachmentById',
        'AssetDirectory must contain exactly the formal GitHub Release assets',
        'Write-GiteeMirrorTelemetry',
        'Gitee Release has duplicate attachments named',
        'size/SHA-256 mismatch for',
        'GITEE_MIRROR SUCCESS',
        'avg_bytes_per_sec=',
        'latest-v2.json.sig',
        'latest-v2.json',
        'latest.json.sig',
        'latest.json'
    )) {
        if (-not $giteeScript.Contains($requiredMirrorBehavior)) { throw "Independent Gitee mirror is missing required recovery/integrity behavior: $requiredMirrorBehavior." }
    }
    if ($giteeScript -match 'actions\.githubusercontent\.com|results-receiver\.actions\.githubusercontent\.com|ssl-no-revoke|NoCheck|RevocationMode|GITHUB_STEP_SUMMARY|git (push|fetch)|git\.exe') {
        throw 'Gitee Mirror must use ordinary REST/Release HTTPS and normal certificate validation.'
    }
    if ($giteeWorkflow -notmatch 'workflow_dispatch:' -or
        $giteeWorkflow -notmatch 'gitee-mirror\.tests\.ps1' -or
        $giteeWorkflow -match 'workflow_run:|secrets\.GITEE_TOKEN|GITHUB_TOKEN:|Publish-GiteeMirror\.ps1\s+-Tag') {
        throw 'Gitee mirror workflow must remain a manual mock-test diagnostic and must not upload official assets.'
    }
    if ($giteeWorkflow -match '(?m)^\s+needs:\s*Formal Release') { throw 'Gitee mirror status must not gate or change GitHub Formal Release status.' }
    foreach ($requiredGithubBehavior in @('Test-GitHubAssetReadback', 'Ensure-GitHubAsset', 'stable Release promotion returned', 'make_latest = ''true''')) {
        if (-not $githubScript.Contains($requiredGithubBehavior)) { throw "GitHub publisher is missing immutable asset/readback behavior: $requiredGithubBehavior." }
    }
    if ($githubScript -match '--clobber|delete.*asset') { throw 'GitHub asset repair must not overwrite or delete an existing attachment.' }
    if ($prepareScript -notmatch 'NETBOOT_UPDATE_SIGNING_PRIVATE_KEY' -or
        $prepareScript -notmatch 'ImportFromPem' -or
        $prepareScript -notmatch 'RSASignaturePadding]::Pss' -or
        $prepareScript -notmatch 'does not match the public key trusted by the client') {
        throw 'The hosted package preparation must sign with the existing client-trusted update key and fail on key mismatch.'
    }
    if ($prepareScript -notmatch 'package-setup\.ps1' -or
        -not $packageBuilder.Contains('NetBootDhcpTool-full-v') -or
        -not $packageBuilder.Contains('sevenZipPackages')) {
        throw 'The formal package path must prepare a paired Setup.exe and signed Full 7z V2 package.'
    }
    $legacyWorkflow = Join-Path $repoRoot 'docs\archive\formal-release-self-hosted-2026-09-28.yml'
    if (-not (Test-Path -LiteralPath $legacyWorkflow) -or
        (Get-Content -Raw -LiteralPath $legacyWorkflow) -notmatch 'runs-on: \[self-hosted, windows, x64, netboot-release\]') {
        throw 'The prior self-hosted Runner workflow must remain archived as a backup.'
    }
    foreach ($legacyScript in @('build/publish-release.ps1', 'build/release-pipeline/ReleaseTransport.psm1', 'build/release-pipeline/ReleaseState.psm1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $legacyScript))) { throw "Legacy release script backup is missing: $legacyScript." }
    }
    if (Test-Path -LiteralPath (Join-Path $repoRoot '.github\workflows\gitee-release-sync.yml')) { throw 'The obsolete Gitee sync workflow must not be restored.' }
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'build\sync-gitee-release.ps1')) { throw 'The obsolete GitHub-download-to-Gitee publisher must not be restored.' }
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
