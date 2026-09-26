[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$AssetsDirectory,

    [string]$GitHubRepository = $env:GITHUB_REPOSITORY
)

$ErrorActionPreference = 'Stop'
$giteeOwner = 'joel20230302'
$giteeRepository = 'NetBootDhcpTool'
$giteeWebRepository = "https://gitee.com/$giteeOwner/$giteeRepository"
$giteeApiRepository = "https://gitee.com/api/v5/repos/$giteeOwner/$giteeRepository"
$token = $env:GITEE_TOKEN

if ([string]::IsNullOrWhiteSpace($token)) { throw 'GITEE_TOKEN is not available to this workflow.' }
if ([string]::IsNullOrWhiteSpace($GitHubRepository)) { throw 'GITHUB_REPOSITORY is not available.' }
if (-not (Test-Path -LiteralPath $AssetsDirectory -PathType Container)) { throw "Release asset directory does not exist: $AssetsDirectory" }

$archiveName = "NetBootDhcpTool-${Tag}.7z"
$archivePath = Join-Path $AssetsDirectory $archiveName
$checksumPath = "${archivePath}.sha256"
$manifestPath = Join-Path $AssetsDirectory 'latest.json'
foreach ($requiredPath in @($archivePath, $checksumPath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Required GitHub release asset is missing: $requiredPath" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = $Tag.Substring(1) -replace '[-+].*$', ''
if ($manifest.version -ne $version -or $manifest.archiveName -ne $archiveName) {
    throw "GitHub latest.json does not describe release ${Tag}."
}
$localArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecarHash = (Get-Content -LiteralPath $checksumPath -Raw).Trim().Split(' ')[0].ToLowerInvariant()
if ($localArchiveHash -ne $sidecarHash -or $localArchiveHash -ne $manifest.archiveSha256.ToLowerInvariant()) {
    throw "GitHub release archive, checksum sidecar, and latest.json disagree for ${Tag}."
}

$githubReleaseJson = & gh release view $Tag --repo $GitHubRepository --json name,body,isPrerelease
if ($LASTEXITCODE -ne 0) { throw "Could not read GitHub Release ${Tag}." }
$githubRelease = $githubReleaseJson | ConvertFrom-Json
if ($githubRelease.isPrerelease) { throw "Gitee synchronization is limited to stable releases; ${Tag} is a prerelease." }

& git fetch origin main --tags
if ($LASTEXITCODE -ne 0) { throw 'Could not fetch GitHub main and release tags.' }
$mainCommit = (& git rev-parse 'refs/remotes/origin/main').Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve GitHub main.' }
$tagCommitRef = "refs/tags/${Tag}^{commit}"
$tagCommit = (& git rev-parse $tagCommitRef).Trim()
if ($LASTEXITCODE -ne 0) { throw "Could not resolve GitHub tag ${Tag}." }
& git merge-base --is-ancestor $tagCommit $mainCommit
if ($LASTEXITCODE -ne 0) { throw "Release tag $Tag is not contained in GitHub main; refusing to move the Gitee branch." }

$askPassScript = Join-Path $AssetsDirectory ("gitee-askpass-{0}.ps1" -f [guid]::NewGuid().ToString('N'))
$askPassCommand = Join-Path $AssetsDirectory ("gitee-askpass-{0}.cmd" -f [guid]::NewGuid().ToString('N'))
$askPassScriptContent = @'
param([string]$Prompt)
if ($Prompt -match '(?i)username') {
    [Console]::WriteLine('joel20230302')
} else {
    [Console]::WriteLine($env:GITEE_TOKEN)
}
'@
Set-Content -LiteralPath $askPassScript -Value $askPassScriptContent -Encoding UTF8
$pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
$cmdLine = '"{0}" -NoLogo -NoProfile -File "{1}" %*' -f $pwshPath, $askPassScript
Set-Content -LiteralPath $askPassCommand -Value "@echo off`r`n$cmdLine`r`n" -Encoding ASCII

try {
    $env:GIT_ASKPASS = $askPassCommand
    $env:GIT_TERMINAL_PROMPT = '0'
    & git push "https://gitee.com/$giteeOwner/$giteeRepository.git" `
        'refs/remotes/origin/main:refs/heads/main' `
        "refs/tags/${Tag}:refs/tags/${Tag}"
    if ($LASTEXITCODE -ne 0) { throw 'Git push to Gitee failed. Check repository access and whether the Gitee main branch can fast-forward.' }
}
finally {
    Remove-Item Env:GIT_ASKPASS -ErrorAction SilentlyContinue
    Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $askPassScript, $askPassCommand -Force -ErrorAction SilentlyContinue
}

$releaseTagPath = [uri]::EscapeDataString($Tag)
$releaseLookupUri = "$giteeApiRepository/releases/tags/$releaseTagPath"
$releaseLookup = Invoke-WebRequest -Uri $releaseLookupUri -Method Get -SkipHttpErrorCheck -TimeoutSec 30
$releaseId = 0
if ($releaseLookup.StatusCode -eq 200) {
    try { $giteeRelease = $releaseLookup.Content | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Gitee Release lookup returned invalid JSON.' }
    if ($null -ne $giteeRelease -and $null -ne $giteeRelease.id -and [long]$giteeRelease.id -gt 0) {
        $releaseId = [long]$giteeRelease.id
        $releaseUri = "$giteeApiRepository/releases/$releaseId"
        $updateForm = @{
            access_token = $token
            tag_name = $Tag
            name = if ([string]::IsNullOrWhiteSpace($githubRelease.name)) { "NetBoot DHCP Tool $Tag" } else { $githubRelease.name }
            body = [string]$githubRelease.body
        }
        $releaseUpdate = Invoke-WebRequest -Uri $releaseUri -Method Patch -Form $updateForm -SkipHttpErrorCheck -TimeoutSec 30
        if ($releaseUpdate.StatusCode -notin @(200, 201)) { throw "Gitee Release metadata update failed with HTTP $($releaseUpdate.StatusCode)." }
    }
} elseif ($releaseLookup.StatusCode -ne 404) {
    throw "Gitee Release lookup failed with HTTP $($releaseLookup.StatusCode)."
}

if ($releaseId -le 0) {
    $createForm = @{
        access_token = $token
        tag_name = $Tag
        name = if ([string]::IsNullOrWhiteSpace($githubRelease.name)) { "NetBoot DHCP Tool $Tag" } else { $githubRelease.name }
        body = [string]$githubRelease.body
        target_commitish = $mainCommit
        prerelease = 'false'
    }
    $releaseCreate = Invoke-WebRequest -Uri "$giteeApiRepository/releases" -Method Post -Body $createForm -SkipHttpErrorCheck -TimeoutSec 30
    if ($releaseCreate.StatusCode -notin @(200, 201)) { throw "Gitee Release creation failed with HTTP $($releaseCreate.StatusCode)." }
    $giteeRelease = $releaseCreate.Content | ConvertFrom-Json
    $releaseId = [long]$giteeRelease.id
}
if ($releaseId -le 0) { throw 'Gitee returned an invalid Release ID.' }

$attachmentsUri = "$giteeApiRepository/releases/$releaseId/attach_files?page=1&per_page=100"
$existingAttachments = Invoke-RestMethod -Uri $attachmentsUri -Method Get -TimeoutSec 30
$wantedNames = @($archiveName, "${archiveName}.sha256", 'latest.json')
foreach ($attachment in $existingAttachments | Where-Object { $_.name -in $wantedNames }) {
    $attachmentId = [long]$attachment.id
    $deleteUri = "$giteeApiRepository/releases/$releaseId/attach_files/$attachmentId`?access_token=$([uri]::EscapeDataString($token))"
    try {
        $deleteResult = Invoke-WebRequest -Uri $deleteUri -Method Delete -SkipHttpErrorCheck -TimeoutSec 30
    } catch {
        throw "Could not replace Gitee attachment '$($attachment.name)' due to an API transport error."
    }
    if ($deleteResult.StatusCode -ne 204) { throw "Could not replace Gitee attachment '$($attachment.name)' (HTTP $($deleteResult.StatusCode))." }
}

function Add-GiteeAttachment([string]$Path) {
    $asset = Get-Item -LiteralPath $Path
    $assetTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "[Gitee] Uploading Release asset '$($asset.Name)' ($($asset.Length) bytes; timeout 900s)."
    $form = @{ access_token = $token; file = $asset }
    try {
        $response = Invoke-RestMethod -Uri "$giteeApiRepository/releases/$releaseId/attach_files" -Method Post -Form $form -TimeoutSec 900
    } catch {
        $assetTimer.Stop()
        throw "Gitee Release asset upload failed or timed out after $([int]$assetTimer.Elapsed.TotalSeconds)s for '$($asset.Name)' ($($asset.Length) bytes). Check the Gitee Release attachment list before retrying."
    }
    $assetTimer.Stop()
    if ($response.name -ne (Split-Path -Leaf $Path) -or [string]::IsNullOrWhiteSpace($response.browser_download_url)) {
        throw "Gitee did not return a usable attachment for $($asset.Name)."
    }
    Write-Host "[Gitee] Uploaded Release asset '$($asset.Name)' in $([int]$assetTimer.Elapsed.TotalSeconds)s."
    return $response
}

$giteeChecksum = Add-GiteeAttachment $checksumPath
$giteeArchive = Add-GiteeAttachment $archivePath
$manifest.downloadUrl = [string]$giteeArchive.browser_download_url
$manifest.downloadMirrors = @("https://github.com/$GitHubRepository/releases/download/$Tag/$archiveName")
$manifest.releasePageUrl = "$giteeWebRepository/releases/tag/$releaseTagPath"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), $utf8NoBom)
$giteeManifest = Add-GiteeAttachment $manifestPath

$expectedAssets = @{
    $archiveName = [long](Get-Item -LiteralPath $archivePath).Length
    "${archiveName}.sha256" = [long](Get-Item -LiteralPath $checksumPath).Length
    'latest.json' = [long](Get-Item -LiteralPath $manifestPath).Length
}
$remoteAttachments = Invoke-RestMethod -Uri $attachmentsUri -Method Get -TimeoutSec 30
foreach ($assetName in $expectedAssets.Keys) {
    $remoteAsset = $remoteAttachments | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
    if ($null -eq $remoteAsset -or [long]$remoteAsset.size -ne $expectedAssets[$assetName]) {
        throw "Gitee release asset readback failed for $assetName."
    }
}

$remoteArchivePath = Join-Path $AssetsDirectory ("gitee-readback-$archiveName")
$readbackTimer = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "[Gitee] Downloading uploaded archive for full SHA-256 verification (timeout 900s)."
Invoke-WebRequest -Uri $giteeArchive.browser_download_url -OutFile $remoteArchivePath -TimeoutSec 900
$readbackTimer.Stop()
Write-Host "[Gitee] Archive readback download completed in $([int]$readbackTimer.Elapsed.TotalSeconds)s."
$remoteArchiveHash = (Get-FileHash -LiteralPath $remoteArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Remove-Item -LiteralPath $remoteArchivePath -Force
if ($remoteArchiveHash -ne $localArchiveHash) { throw "Gitee archive readback SHA-256 mismatch for ${Tag}." }
$remoteSidecarContent = Invoke-WebRequest -Uri $giteeChecksum.browser_download_url -TimeoutSec 30
$remoteSidecarHash = ($remoteSidecarContent.Content.Trim().Split(' ')[0]).ToLowerInvariant()
if ($remoteSidecarHash -ne $localArchiveHash) { throw "Gitee checksum sidecar readback mismatch for ${Tag}." }
$remoteManifestContent = Invoke-WebRequest -Uri $giteeManifest.browser_download_url -TimeoutSec 30
$remoteManifest = $remoteManifestContent.Content | ConvertFrom-Json
if ($remoteManifest.version -ne $version -or $remoteManifest.archiveSha256 -ne $localArchiveHash -or $remoteManifest.downloadUrl -ne $giteeArchive.browser_download_url) {
    throw "Gitee latest.json readback does not match Gitee archive ${Tag}."
}

# Publish the same dual-source manifest on GitHub so clients can compare both mirrors
# even when the Gitee release API is temporarily unreachable.
& gh release upload $Tag $manifestPath --repo $GitHubRepository --clobber
if ($LASTEXITCODE -ne 0) { throw "Could not update GitHub latest.json for ${Tag}." }
$githubReadbackDirectory = Join-Path $AssetsDirectory 'github-readback'
New-Item -ItemType Directory -Force -Path $githubReadbackDirectory | Out-Null
& gh release download $Tag --repo $GitHubRepository --pattern 'latest.json' --dir $githubReadbackDirectory
if ($LASTEXITCODE -ne 0) { throw "Could not read back GitHub latest.json for ${Tag}." }
$githubManifestPath = Join-Path $githubReadbackDirectory 'latest.json'
$githubManifest = Get-Content -LiteralPath $githubManifestPath -Raw | ConvertFrom-Json
if (($githubManifest.version -ne $version) -or
    ($githubManifest.archiveSha256 -ne $localArchiveHash) -or
    ($githubManifest.downloadUrl -ne $giteeArchive.browser_download_url) -or
    ($githubManifest.downloadMirrors -notcontains "https://github.com/$GitHubRepository/releases/download/$Tag/$archiveName")) {
    throw "GitHub latest.json readback does not contain the verified Gitee and GitHub download sources for ${Tag}."
}

Write-Host "Gitee Release synchronized and verified: $Tag"
Write-Host "Gitee archive: $($giteeArchive.browser_download_url)"
Write-Host "Gitee checksum: $($giteeChecksum.browser_download_url)"
Write-Host "Gitee manifest: $($giteeManifest.browser_download_url)"
Write-Host "GitHub latest.json now advertises both download sources."
