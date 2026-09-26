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
$giteeAuthHeaders = if ([string]::IsNullOrWhiteSpace($token)) { @{} } else { @{ Authorization = "Bearer $token" } }
$curlExecutable = (Get-Command curl.exe -ErrorAction Stop).Source

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
        $releaseName = if ([string]::IsNullOrWhiteSpace($githubRelease.name)) { "NetBoot DHCP Tool $Tag" } else { $githubRelease.name }
        $releaseBody = [string]$githubRelease.body
        if ([string]$giteeRelease.name -cne $releaseName -or [string]$giteeRelease.body -cne $releaseBody) {
            $releaseUri = "$giteeApiRepository/releases/$releaseId"
            $updateForm = @{ access_token = $token; tag_name = $Tag; name = $releaseName; body = $releaseBody }
            $releaseUpdate = Invoke-WebRequest -Uri $releaseUri -Method Patch -Form $updateForm -Headers $giteeAuthHeaders -SkipHttpErrorCheck -TimeoutSec 30
            if ($releaseUpdate.StatusCode -notin @(200, 201)) { throw "Gitee Release metadata update failed with HTTP $($releaseUpdate.StatusCode)." }
        } else {
            Write-Host "[Gitee] Existing Release metadata already matches GitHub; skipping update."
        }
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
function Get-GiteeAttachments() {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            return Invoke-RestMethod -Uri $attachmentsUri -Method Get -TimeoutSec 60
        } catch {
            if ($attempt -eq 5) { throw "Gitee attachment list readback failed after $attempt attempts." }
            Write-Host "[Gitee] Attachment list request attempt $attempt failed; retrying in 5 seconds."
            Start-Sleep -Seconds 5
        }
    }
}

$existingAttachments = Get-GiteeAttachments
function Remove-GiteeAttachment([object]$Attachment) {
    $attachmentId = [long]$Attachment.id
    $deleteUri = "$giteeApiRepository/releases/$releaseId/attach_files/$attachmentId`?access_token=$([uri]::EscapeDataString($token))"
    try {
        $deleteResult = Invoke-WebRequest -Uri $deleteUri -Method Delete -SkipHttpErrorCheck -TimeoutSec 30
    } catch {
        throw "Could not remove Gitee attachment '$($Attachment.name)' due to an API transport error."
    }
    if ($deleteResult.StatusCode -ne 204) { throw "Could not remove Gitee attachment '$($Attachment.name)' (HTTP $($deleteResult.StatusCode))." }
}

function ConvertFrom-GiteeAttachmentContent([object]$Content) {
    if ($null -eq $Content) { return '' }
    if ($Content -is [byte[]]) { return [System.Text.Encoding]::UTF8.GetString($Content) }
    return [string]$Content
}

$replaceNames = @("${archiveName}.sha256", 'latest.json')
foreach ($attachment in $existingAttachments | Where-Object { $_.name -in $replaceNames }) {
    Remove-GiteeAttachment $attachment
}
$expectedArchiveSize = [long](Get-Item -LiteralPath $archivePath).Length
$existingArchive = $existingAttachments |
    Where-Object { $_.name -eq $archiveName } |
    Sort-Object { [long]$_.id } -Descending |
    Select-Object -First 1
$reuseExistingArchive = ($null -ne $existingArchive -and [long]$existingArchive.size -eq $expectedArchiveSize)
if (-not $reuseExistingArchive) {
    foreach ($attachment in $existingAttachments | Where-Object { $_.name -eq $archiveName }) {
        Remove-GiteeAttachment $attachment
    }
}

function Add-GiteeAttachment([string]$Path) {
    $asset = Get-Item -LiteralPath $Path
    $assetTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "[Gitee] Uploading Release asset '$($asset.Name)' ($($asset.Length) bytes via curl.exe over HTTP/1.1; up to 1800s per attempt)."
    $curlConfigPath = Join-Path $AssetsDirectory ("gitee-curl-{0}.conf" -f [guid]::NewGuid().ToString('N'))
    $curlErrorPath = Join-Path $AssetsDirectory ("gitee-curl-{0}.stderr" -f [guid]::NewGuid().ToString('N'))
    if ($token -match '[\r\n]') { throw 'GITEE_TOKEN contains a line break and cannot be passed to curl safely.' }
    $escapedToken = $token.Replace('\', '\\').Replace('"', '\"')
    $responseContent = ''
    $curlError = ''
    $curlExitCode = -1
    try {
        # Keep the token out of curl's process arguments and logs. The file is
        # short-lived and removed in finally; curl builds the multipart boundary.
        Set-Content -LiteralPath $curlConfigPath -Value ('form = "access_token={0}"' -f $escapedToken) -Encoding ascii
        $curlArguments = @(
            '--config', $curlConfigPath,
            '--silent', '--show-error', '--http1.1',
            '--connect-timeout', '20', '--max-time', '1800',
            '--retry', '5', '--retry-delay', '5', '--retry-all-errors',
            '--fail-with-body', '--request', 'POST',
            '--form', "file=@$($asset.FullName);filename=$($asset.Name)",
            "$giteeApiRepository/releases/$releaseId/attach_files"
        )
        $responseContent = (& $curlExecutable @curlArguments 2> $curlErrorPath | Out-String).Trim()
        $curlExitCode = $LASTEXITCODE
        if (Test-Path -LiteralPath $curlErrorPath -PathType Leaf) {
            $curlError = [System.IO.File]::ReadAllText($curlErrorPath).Trim()
        }
    } catch {
        $assetTimer.Stop()
        $message = $_.Exception.Message
        if (-not [string]::IsNullOrWhiteSpace($token)) { $message = $message.Replace($token, '[REDACTED]') }
        throw "Gitee curl upload could not start for '$($asset.Name)': $message"
    } finally {
        Remove-Item -LiteralPath $curlConfigPath, $curlErrorPath -Force -ErrorAction SilentlyContinue
    }
    $assetTimer.Stop()
    if ($curlExitCode -ne 0) {
        # A connection can be lost after Gitee has stored the file. Check the
        # attachment list before failing or asking the workflow to retry.
        try {
            $readback = Get-GiteeAttachments
            $storedAsset = $readback |
                Where-Object { $_.name -eq $asset.Name -and [long]$_.size -eq [long]$asset.Length -and -not [string]::IsNullOrWhiteSpace($_.browser_download_url) } |
                Sort-Object { [long]$_.id } -Descending |
                Select-Object -First 1
            if ($null -ne $storedAsset) {
                Write-Host "[Gitee] Upload response was interrupted, but attachment readback found '$($asset.Name)' ($($asset.Length) bytes) after $([int]$assetTimer.Elapsed.TotalSeconds)s."
                return $storedAsset
            }
        } catch {
            Write-Host '[Gitee] Attachment readback after the curl error was unavailable.'
        }
        $errorBody = $responseContent
        if (-not [string]::IsNullOrWhiteSpace($token)) { $errorBody = $errorBody.Replace($token, '[REDACTED]') }
        if ($errorBody.Length -gt 400) { $errorBody = $errorBody.Substring(0, 400) }
        if (-not [string]::IsNullOrWhiteSpace($token)) { $curlError = $curlError.Replace($token, '[REDACTED]') }
        if ($curlError.Length -gt 400) { $curlError = $curlError.Substring(0, 400) }
        throw "Gitee curl upload failed with exit code $curlExitCode for '$($asset.Name)' ($($asset.Length) bytes) after $([int]$assetTimer.Elapsed.TotalSeconds)s. Response: $errorBody. curl: $curlError. Check the Gitee Release attachment list before retrying."
    }
    try { $response = $responseContent | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Gitee curl upload returned invalid JSON for '$($asset.Name)'." }
    if ($response.name -ne (Split-Path -Leaf $Path) -or [string]::IsNullOrWhiteSpace($response.browser_download_url)) {
        throw "Gitee did not return a usable attachment for $($asset.Name)."
    }
    Write-Host "[Gitee] Uploaded Release asset '$($asset.Name)' in $([int]$assetTimer.Elapsed.TotalSeconds)s."
    return $response
}

$giteeChecksum = Add-GiteeAttachment $checksumPath
if ($reuseExistingArchive) {
    $giteeArchive = $existingArchive
    Write-Host "[Gitee] Reusing existing archive attachment '$archiveName' ($expectedArchiveSize bytes); full SHA-256 readback is still required."
} else {
    $giteeArchive = Add-GiteeAttachment $archivePath
}
$manifest | Add-Member -NotePropertyName downloadMirrors -NotePropertyValue @() -Force
$manifest.downloadUrl = [string]$giteeArchive.browser_download_url
$manifest.downloadMirrors = @("https://github.com/$GitHubRepository/releases/download/$Tag/$archiveName")
$manifest.releasePageUrl = "$giteeWebRepository/releases/tag/$releaseTagPath"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), $utf8NoBom)

$expectedAssets = @{
    $archiveName = [long](Get-Item -LiteralPath $archivePath).Length
    "${archiveName}.sha256" = [long](Get-Item -LiteralPath $checksumPath).Length
}
$remoteAttachments = Get-GiteeAttachments
foreach ($assetName in $expectedAssets.Keys) {
    $remoteAsset = $remoteAttachments | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
    if ($null -eq $remoteAsset -or [long]$remoteAsset.size -ne $expectedAssets[$assetName]) {
        throw "Gitee release asset readback failed for $assetName."
    }
}

$remoteArchivePath = Join-Path $AssetsDirectory ("gitee-readback-$archiveName")
$readbackTimer = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host '[Gitee] Downloading uploaded archive for full SHA-256 verification (curl HTTP/1.1, timeout 1800s per attempt).'
$readbackStats = & $curlExecutable --silent --show-error --location --http1.1 `
    --connect-timeout 20 --max-time 1800 --retry 5 --retry-delay 5 --retry-all-errors --fail-with-body `
    --output $remoteArchivePath --write-out 'http=%{http_code} bytes=%{size_download} seconds=%{time_total}' `
    $giteeArchive.browser_download_url
$readbackExitCode = $LASTEXITCODE
$readbackTimer.Stop()
if ($readbackExitCode -ne 0) { throw "Gitee archive readback download failed with curl exit code ${readbackExitCode}: $readbackStats" }
Write-Host "[Gitee] Archive readback download completed in $([int]$readbackTimer.Elapsed.TotalSeconds)s ($readbackStats)."
$remoteArchiveHash = (Get-FileHash -LiteralPath $remoteArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Remove-Item -LiteralPath $remoteArchivePath -Force
if ($remoteArchiveHash -ne $localArchiveHash) {
    Remove-GiteeAttachment $giteeArchive
    throw "Gitee archive readback SHA-256 mismatch for ${Tag}; the mismatched attachment was removed."
}
$remoteSidecarContent = Invoke-WebRequest -Uri $giteeChecksum.browser_download_url -TimeoutSec 30
$remoteSidecarText = ConvertFrom-GiteeAttachmentContent $remoteSidecarContent.Content
$remoteSidecarHash = ($remoteSidecarText.Trim().Split(' ')[0]).ToLowerInvariant()
if ($remoteSidecarHash -ne $localArchiveHash) { throw "Gitee checksum sidecar readback mismatch for ${Tag}." }
$giteeManifest = Add-GiteeAttachment $manifestPath
$remoteAttachments = Get-GiteeAttachments
$remoteManifestAsset = $remoteAttachments | Where-Object { $_.name -eq 'latest.json' } | Select-Object -First 1
if ($null -eq $remoteManifestAsset -or [long]$remoteManifestAsset.size -ne [long](Get-Item -LiteralPath $manifestPath).Length) {
    throw 'Gitee latest.json attachment size readback failed.'
}
$remoteManifestContent = Invoke-WebRequest -Uri $giteeManifest.browser_download_url -TimeoutSec 30
$remoteManifestText = ConvertFrom-GiteeAttachmentContent $remoteManifestContent.Content
$remoteManifest = $remoteManifestText | ConvertFrom-Json
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
