[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$StateRoot,
    [string]$GitHubRepository = $env:GITHUB_REPOSITORY,
    [string]$GiteeOwner = 'joel20230302',
    [string]$GiteeRepository = 'NetBootDhcpTool',
    [AllowEmptyString()][string]$RepairManifestSha256 = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'release-pipeline\ReleaseState.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'release-pipeline\ReleaseTransport.psm1') -Force
$script:releaseWarnings = [System.Collections.Generic.List[string]]::new()
$script:verifiedAssets = @{}
$script:releaseStages = [ordered]@{}
foreach ($stageName in @('Build', 'GitHub Publish', 'GitHub Verify', 'Gitee Publish', 'Gitee Verify', 'Manifest Publish', 'Final Verify')) {
    $script:releaseStages[$stageName] = 'Not run'
}

function Add-ReleaseWarning([string]$Message) {
    $script:releaseWarnings.Add($Message)
    Write-Host "[Auxiliary] $Message"
}

function Start-ReleaseStage([string]$Name) {
    if ($script:currentStage) {
        $script:releaseStages[$script:currentStage] = 'Success'
        Write-Host '::endgroup::'
    }
    $script:currentStage = $Name
    $script:releaseStages[$Name] = 'Running'
    Write-Host "::group::$Name"
}

function Write-ReleaseSummary([bool]$Succeeded) {
    if ($script:currentStage) {
        $script:releaseStages[$script:currentStage] = $(if ($Succeeded) { 'Success' } else { 'Failed' })
        Write-Host '::endgroup::'
    }
    $lines = @("## Formal release $Tag", '', '| Stage | Result |', '| --- | --- |')
    foreach ($entry in $script:releaseStages.GetEnumerator()) { $lines += "| $($entry.Key) | $($entry.Value) |" }
    if ($Succeeded) {
        foreach ($message in $script:releaseWarnings) {
            $escaped = $message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
            Write-Host "::warning::$escaped"
            $lines += "- Warning: $message"
        }
    }
    if ($env:GITHUB_STEP_SUMMARY) {
        try { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n") -Encoding utf8 }
        catch { Write-Warning 'Could not write the optional Actions summary; release validation result is unchanged.' }
    }
    Write-Host ($lines -join "`n")
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$version = Get-ReleaseVersionFromTag $Tag
$expectedArchiveName = Get-ReleaseArchiveName $Tag
. (Join-Path $PSScriptRoot 'release-identity.ps1')
$projectVersion = (Get-NetBootVersionMetadata -RepositoryRoot $repoRoot).Version
if ($projectVersion -cne $version) { throw "Tag $Tag does not match application version $projectVersion." }
if ($SourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'SourceCommit must be a full Git SHA.' }
if ($RepairManifestSha256 -and $RepairManifestSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Manifest repair requires the exact old SHA-256.' }
if ([string]::IsNullOrWhiteSpace($GitHubRepository) -or $GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'GitHubRepository must be in owner/repository form.'
}
if ($GiteeOwner -notmatch '^[A-Za-z0-9_.-]+$' -or $GiteeRepository -notmatch '^[A-Za-z0-9_.-]+$') {
    throw 'Gitee owner and repository must contain only letters, digits, dots, underscores, or hyphens.'
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN is not available to the release job.' }
if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) { throw 'GITEE_TOKEN is not available to the release job.' }
if ($env:GITEE_TOKEN -match '[\r\n]') { throw 'GITEE_TOKEN contains a line break and cannot be passed safely.' }
$signingKeyPath = if ([string]::IsNullOrWhiteSpace($env:NETBOOT_UPDATE_SIGNING_KEY)) {
    Join-Path $env:LOCALAPPDATA 'NetBootDhcpTool\release-signing\update-signing-private.pem'
} else { [System.IO.Path]::GetFullPath($env:NETBOOT_UPDATE_SIGNING_KEY) }
if (-not (Test-Path -LiteralPath $signingKeyPath -PathType Leaf)) {
    throw "Update signing key is missing at the protected release-runner path: $signingKeyPath. No remote release assets have been changed."
}
$script:updateSigningRsa = [System.Security.Cryptography.RSA]::Create()
try {
    $signingPem = [System.IO.File]::ReadAllText($signingKeyPath)
    $script:updateSigningRsa.ImportFromPem($signingPem)
    if ($script:updateSigningRsa.KeySize -lt 3072) { throw 'Update signing key must be RSA 3072-bit or stronger.' }
    $coreSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\NetBootDhcpTool.Core\UpdatePackages.cs') -Raw
    $publicMatch = [regex]::Match($coreSource, '(?ms)public const string TrustedPublicKeyPem = """\r?\n(?<pem>.*?)\r?\n""";')
    if (-not $publicMatch.Success) { throw 'Could not read the trusted public key from the client source.' }
    $trustedRsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $trustedRsa.ImportFromPem($publicMatch.Groups['pem'].Value)
        $privatePublic = $script:updateSigningRsa.ExportParameters($false)
        $trustedPublic = $trustedRsa.ExportParameters($false)
        if ([Convert]::ToBase64String($privatePublic.Modulus) -cne [Convert]::ToBase64String($trustedPublic.Modulus) -or
            [Convert]::ToBase64String($privatePublic.Exponent) -cne [Convert]::ToBase64String($trustedPublic.Exponent)) {
            throw 'Update signing key does not match the public key compiled into the client.'
        }
    } finally { $trustedRsa.Dispose() }
} catch {
    $script:updateSigningRsa.Dispose()
    throw "Update signing key validation failed. No remote release assets have been changed. $($_.Exception.Message)"
}

$fullRepoRoot = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$fullStateRoot = [System.IO.Path]::GetFullPath($StateRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
if ($fullStateRoot -ceq $fullRepoRoot -or $fullStateRoot.StartsWith("$fullRepoRoot$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'StateRoot must be outside the checkout so a runner job can clean its workspace without losing release recovery data.'
}
New-Item -ItemType Directory -Path $fullStateRoot -Force | Out-Null
$stateDirectory = Join-Path $fullStateRoot $Tag
$statePath = Join-Path $stateDirectory 'release-state.json'

function Remove-OwnedTemporaryDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedParent,
        [Parameter(Mandatory)][string]$RequiredNamePrefix
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $fullParent = [System.IO.Path]::GetFullPath($ExpectedParent).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if ([System.IO.Path]::GetDirectoryName($fullPath) -cne $fullParent -or
        -not ([System.IO.Path]::GetFileName($fullPath).StartsWith($RequiredNamePrefix, [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing to remove a temporary directory outside its owned release state: $fullPath"
    }
    try { Remove-Item -LiteralPath $fullPath -Recurse -Force }
    catch { Add-ReleaseWarning "Temporary readback cleanup failed for $([System.IO.Path]::GetFileName($fullPath))." }
}

function Invoke-GitChecked {
    param([Parameter(Mandatory)][string[]]$Arguments)

    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $output = & git @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0 -or $Arguments[0] -notin @('fetch', 'push') -or $attempt -eq 4 -or -not (Test-GitHubTransientError $output)) { break }
        Write-Host "[Retry] Git $($Arguments[0]) transport failed; retrying the same non-forced ref operation ($attempt/4)."
        Start-Sleep -Seconds (Get-ReleaseRetryDelay -Attempt $attempt -Jitter)
    }
    if ($exitCode -ne 0) {
        throw "Git command failed (exit $exitCode): $($output.Trim())"
    }
    return $output.Trim()
}

function Invoke-Gh {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $retrySafe = ($Arguments[0] -eq 'api' -and $Arguments -notcontains '--method' -and $Arguments -notcontains '-X') -or ($Arguments[0] -eq 'release' -and $Arguments[1] -in @('view', 'download'))
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $output = & gh @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0 -or -not $retrySafe -or $attempt -eq 4 -or -not (Test-GitHubTransientError $output)) { break }
        Write-Host "[Retry] GitHub $($Arguments[0]) $($Arguments[1]) transient failure; attempt $attempt/4."
        Start-Sleep -Seconds (Get-ReleaseRetryDelay -Attempt $attempt -Jitter)
    }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "GitHub CLI command failed (exit $exitCode; category=$(Get-ReleaseTransportCategory $output))."
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output.Trim() }
}

function Test-GitHubTransientError([string]$Message) {
    if ((Get-ReleaseTransportCategory $Message) -eq 'certificate' -or $Message -match 'HTTP (401|403|404)') { return $false }
    return $Message -match '(?i)HTTP (408|429|5\d\d)' -or (Get-ReleaseTransportCategory $Message) -in @('eof','connect-timeout','read-timeout','timeout','connection-reset','tls-interruption','connect')
}

function Get-GitHubRelease {
    $response = Invoke-ReleaseHttp -Uri "https://api.github.com/repos/$GitHubRepository/releases/tags/$Tag" -Headers @{Authorization="Bearer $env:GH_TOKEN";Accept='application/vnd.github+json'} -Stage 'legacy-github-api'
    if ($response.StatusCode -eq 404) { return $null }
    if ($response.StatusCode -ne 200) { throw "GitHub Release lookup returned HTTP $($response.StatusCode)." }
    try { $release = $response.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { throw 'GitHub Release lookup returned invalid JSON.' }
    foreach ($asset in $release.assets) { $asset.apiUrl = $asset.url }
    return @{tagName=$release.tag_name;name=$release.name;body=$release.body;isDraft=$release.draft;isPrerelease=$release.prerelease;assets=$release.assets}
}

function Get-GitHubAsset([string]$Name) {
    $release = Get-GitHubRelease
    if ($null -eq $release) { return $null }
    $assets = @($release.assets | Where-Object { $_.name -ceq $Name })
    if ($assets.Count -gt 1) { throw "GitHub has duplicate attachments named $Name." }
    if ($assets.Count -eq 0) { return $null }
    return $assets[0]
}

function Test-DownloadedAsset {
    param(
        [Parameter(Mandatory)][string]$RemotePath,
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    if (-not (Test-Path -LiteralPath $RemotePath -PathType Leaf)) { throw "Remote readback did not create $AssetName." }
    if ((Get-Item -LiteralPath $RemotePath).Length -ne (Get-Item -LiteralPath $LocalPath).Length) {
        throw "Remote readback size mismatch for $AssetName."
    }
    if ((Get-ReleaseFileSha256 $RemotePath) -ne (Get-ReleaseFileSha256 $LocalPath)) {
        throw "Remote readback SHA-256 mismatch for $AssetName."
    }
    if ($AssetName -ceq $expectedArchiveName -and (Get-ReleaseFileSha256 $RemotePath) -ne $ArchiveSha256) {
        throw "Remote archive SHA-256 does not match the release state for $AssetName."
    }
    if ($AssetName -ceq "$expectedArchiveName.sha256") {
        $sidecarParts = (Get-Content -LiteralPath $RemotePath -Raw).Trim() -split '\s+', 2
        if ($sidecarParts.Count -ne 2 -or $sidecarParts[0].ToLowerInvariant() -ne $ArchiveSha256 -or $sidecarParts[1] -cne $expectedArchiveName) {
            throw "Remote checksum sidecar content is invalid for $Tag."
        }
    }
}

function Assert-ReleaseAssetFileName {
    param([Parameter(Mandatory)][string]$LocalPath, [Parameter(Mandatory)][string]$Name)

    if ([System.IO.Path]::GetFileName($LocalPath) -cne $Name) {
        throw "The local asset filename must match the remote asset name '$Name'."
    }
}

function Download-GitHubAsset([string]$Name, [string]$DestinationDirectory) {
    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $asset = Get-GitHubAsset $Name
    if ($null -eq $asset) { throw 'GitHub asset is absent.' }
    $path = Join-Path $DestinationDirectory $Name
    $digest = if ($asset.digest -match '^sha256:([a-fA-F0-9]{64})$') { $Matches[1] } else { '' }
    # Anonymous public readback; when old API metadata has no digest, callers
    # still compare the completed file to the immutable local bundle. A Python
    # binary fallback is refused without both expected size and SHA-256.
    Invoke-ReleaseDownload -Uri ([string]$asset.browser_download_url) -Destination $path -AssetName $Name -ExpectedSize ([long]$asset.size) -ExpectedSha256 $digest -Stage 'legacy-github-download'
    return $path
}

function Ensure-GitHubAsset {
    param(
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    Assert-ReleaseAssetFileName -LocalPath $LocalPath -Name $Name
    $asset = Get-GitHubAsset $Name
    if ($null -ne $asset) {
        if ([long]$asset.size -ne [long](Get-Item -LiteralPath $LocalPath).Length) {
            throw "GitHub already has a different-sized $Name; the release is immutable and will not be overwritten."
        }
        Write-Host "[GitHub] Reusing existing $Name after full download and SHA-256 verification."
    } else {
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            $upload = Invoke-Gh -Arguments @('release', 'upload', $Tag, $LocalPath, '--repo', $GitHubRepository) -AllowFailure
            if ($upload.ExitCode -eq 0) { break }
            # GitHub enforces unique asset names. Reconcile after every interrupted
            # request; never use --clobber for an upload or overwrite different bytes.
            Start-Sleep -Seconds (Get-ReleaseRetryDelay -Attempt $attempt -Jitter)
            $asset = Get-GitHubAsset $Name
            if ($null -ne $asset) {
                if ([long]$asset.size -ne [long](Get-Item -LiteralPath $LocalPath).Length) {
                    throw "GitHub upload outcome for $Name has a different size; refusing another upload."
                }
                Add-ReleaseWarning "GitHub upload response for $Name was interrupted; recovered the stored asset without repeating the upload."
                break
            }
            if ($attempt -eq 4 -or -not (Test-GitHubTransientError $upload.Output)) {
                throw "GitHub upload failed for $Name after $attempt attempt(s); no matching asset is visible. $($upload.Output)"
            }
            Write-Host "[Retry] GitHub confirmed $Name absent; retrying the same immutable filename ($attempt/4)."
        }
    }

    $readbackDirectory = Join-Path $stateDirectory ("github-readback-" + [guid]::NewGuid().ToString('N'))
    try {
        $remotePath = Download-GitHubAsset $Name $readbackDirectory
        Test-DownloadedAsset -RemotePath $remotePath -LocalPath $LocalPath -AssetName $Name -ArchiveSha256 $ArchiveSha256
        $script:verifiedAssets["GitHub/$Name"] = Get-ReleaseFileSha256 $remotePath
        Write-Host "[GitHub] Verified $Name ($((Get-Item -LiteralPath $remotePath).Length) bytes)."
    } finally {
        Remove-OwnedTemporaryDirectory -Path $readbackDirectory -ExpectedParent $stateDirectory -RequiredNamePrefix 'github-readback-'
    }
}

function Get-GiteeHeaders {
    return @{ Authorization = "Bearer $env:GITEE_TOKEN"; Accept = 'application/json' }
}

function Get-GiteeResponseErrorSummary([object]$Response) {
    $body = [string]$Response.Content
    if ([string]::IsNullOrWhiteSpace($body)) { return '' }

    foreach ($secretValue in @([string]$env:GITEE_TOKEN, [uri]::EscapeDataString([string]$env:GITEE_TOKEN))) {
        if (-not [string]::IsNullOrEmpty($secretValue)) {
            $body = $body.Replace($secretValue, '[redacted]')
        }
    }
    $body = ($body -replace '\s+', ' ').Trim()
    if ($body.Length -gt 512) { $body = $body.Substring(0, 512) + '…' }
    return "; response: $body"
}

function Invoke-GiteeJsonRequest {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][ValidateSet('Post', 'Patch')][string]$Method,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Payload
    )

    $jsonBody = ConvertTo-Json -InputObject $Payload -Depth 8 -Compress
    return Invoke-ReleaseHttp -Uri (Assert-SafeGiteeDownloadUrl $Uri) -Method $Method -Headers (Get-GiteeHeaders) `
        -ContentType 'application/json; charset=utf-8' -Body $jsonBody
}

function Get-GiteeReleaseByTag {
    $encodedTag = [uri]::EscapeDataString($Tag)
    $uri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/tags/$encodedTag"
    $response = Invoke-ReleaseHttp -Uri (Assert-SafeGiteeDownloadUrl $uri) -Headers (Get-GiteeHeaders)
    if ($response.StatusCode -eq 404) { return $null }
    if ($response.StatusCode -ne 200) { throw "Gitee Release lookup returned HTTP $($response.StatusCode)." }
    if ([string]::IsNullOrWhiteSpace($response.Content)) { throw 'Gitee Release lookup returned an empty response.' }
    if ($response.Content.Trim() -eq 'null') { return $null }
    try { return $response.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { throw "Gitee Release lookup returned invalid JSON for $Tag." }
}

function Ensure-GiteeRelease {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$BaseManifest)

    $release = Get-GiteeReleaseByTag
    $createdByPipeline = $false
    if ($null -eq $release) {
        $mainCommit = (Invoke-GitChecked -Arguments @('rev-parse', 'refs/remotes/origin/main')).Trim()
        $payload = New-GiteeReleasePayload -Operation Create -Tag $Tag -Name "NetBoot DHCP Tool $Tag" `
            -Body ([string]$BaseManifest.releaseNotes) -TargetCommit $mainCommit
        try {
            $created = Invoke-GiteeJsonRequest -Uri "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases" -Method Post -Payload $payload
        } catch { $created = $null }
        if ($null -eq $created -or $created.StatusCode -notin @(200, 201)) {
            $release = Get-GiteeReleaseByTag
            if ($null -eq $release) {
                $status = if ($null -eq $created) { 'transport failure' } else { "HTTP $($created.StatusCode)$(Get-GiteeResponseErrorSummary $created)" }
                throw "Gitee Release creation failed ($status); no Release was confirmed."
            }
            Add-ReleaseWarning 'Gitee Release creation response failed; the same tag was recovered before uploading.'
        } else {
            try { $release = $created.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
            catch { throw 'Gitee Release creation returned invalid JSON; rerun will reconcile the tag.' }
        }
        $createdByPipeline = $null -ne $created -and $created.StatusCode -in @(200, 201)
        Write-Host "[Gitee] Created Release $Tag."
    }

    $releaseId = [long]$release.id
    if ($releaseId -le 0) { throw "Gitee Release lookup returned an invalid ID for $Tag." }
    $releaseIdChanged = $null -ne $script:releaseState.giteeReleaseId -and [long]$script:releaseState.giteeReleaseId -ne $releaseId
    if ($releaseIdChanged) {
        $script:releaseState.giteeAssets = [ordered]@{}
        $script:releaseState.giteeReleaseCreatedByPipeline = [bool]$createdByPipeline
    }
    $script:releaseState.giteeReleaseId = $releaseId
    $script:releaseState.giteeReleaseCreatedByPipeline = [bool]($script:releaseState.giteeReleaseCreatedByPipeline -or $createdByPipeline)
    Save-ReleaseState -Path $statePath -State $script:releaseState

    $expectedName = "NetBoot DHCP Tool $Tag"
    $expectedBody = [string]$BaseManifest.releaseNotes
    $normalizedCurrentBody = ([string]$release.body -replace "`r`n?", "`n").TrimEnd()
    $normalizedExpectedBody = ($expectedBody -replace "`r`n?", "`n").TrimEnd()
    if ([string]$release.name -cne $expectedName -or $normalizedCurrentBody -cne $normalizedExpectedBody) {
        $payload = New-GiteeReleasePayload -Operation Update -Tag $Tag -Name $expectedName -Body $expectedBody
        try {
            $update = Invoke-GiteeJsonRequest -Uri "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$releaseId" -Method Patch -Payload $payload
        } catch { throw 'Gitee Release metadata update failed; no existing assets were removed.' }
        if ($update.StatusCode -notin @(200, 201)) {
            $errorSummary = Get-GiteeResponseErrorSummary $update
            throw "Gitee Release metadata update returned HTTP $($update.StatusCode)$errorSummary."
        }
        $release = $update.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        if ($null -eq $release -or [long]$release.id -ne $releaseId) { throw 'Gitee Release metadata readback did not resolve to the same Release ID.' }
    }
    return $release
}

function Get-GiteeAttachmentById {
    param([Parameter(Mandatory)][long]$ReleaseId, [Parameter(Mandatory)][long]$AttachmentId)

    $uri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$ReleaseId/attach_files/$AttachmentId"
    try { $response = Invoke-ReleaseHttp -Uri $uri -Headers (Get-GiteeHeaders) -MaximumAttempts 2 -TimeoutSec 15 }
    catch { return [pscustomobject]@{ Available = $false; Attachment = $null; StatusCode = 0 } }
    if ($response.StatusCode -eq 404) { return [pscustomobject]@{ Available = $true; Attachment = $null; StatusCode = 404 } }
    if ($response.StatusCode -ne 200) { return [pscustomobject]@{ Available = $false; Attachment = $null; StatusCode = [int]$response.StatusCode } }
    try { $attachment = $response.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { return [pscustomobject]@{ Available = $false; Attachment = $null; StatusCode = 200 } }
    $returnedId = 0L
    if (-not [long]::TryParse([string]$attachment.id, [ref]$returnedId) -or $returnedId -ne $AttachmentId) {
        throw "Gitee single-attachment lookup returned an unexpected ID for requested attachment $AttachmentId."
    }
    return [pscustomobject]@{ Available = $true; Attachment = $attachment; StatusCode = 200 }
}

function Get-GiteeAttachments {
    $all = @()
    for ($page = 1; $page -le 10; $page++) {
        $uri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$($script:releaseState.giteeReleaseId)/attach_files?page=$page&per_page=100"
        $response = Invoke-ReleaseHttp -Uri $uri -Headers (Get-GiteeHeaders)
        if ($response.StatusCode -ne 200) { throw "Gitee attachment-list recovery returned HTTP $($response.StatusCode)." }
        if (-not ([string]$response.Content).TrimStart().StartsWith('[')) { throw 'Gitee attachment-list recovery did not return an array.' }
        try { $items = @($response.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop) }
        catch { throw 'Gitee attachment-list recovery returned invalid JSON.' }
        $all += $items
        if ($items.Count -lt 100) { return $all }
    }
    throw 'Gitee attachment-list recovery exceeded its pagination bound; absence cannot be established.'
}

function Invoke-CurlDownload {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$AssetName
    )

    $safeUri = Assert-SafeGiteeDownloadUrl $Uri
    $null = Invoke-ReleaseDownload -Uri $safeUri -Destination $Destination -AssetName $AssetName
}

function Get-GiteeAssetReadback {
    param(
        [Parameter(Mandatory)][long]$ReleaseId,
        [Parameter(Mandatory)][long]$AttachmentId,
        [AllowNull()][AllowEmptyString()][string]$FallbackUrl,
        [Parameter(Mandatory)][string]$AssetName
    )

    $destination = Join-Path $stateDirectory ("gitee-readback-" + [guid]::NewGuid().ToString('N'))
    $downloadUri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$ReleaseId/attach_files/$AttachmentId/download"
    try {
        # The upload response/checkpoint URL is also the URL clients use. Verify
        # those exact public bytes first; a second metadata/list call is unnecessary.
        $primaryUri = if ([string]::IsNullOrWhiteSpace($FallbackUrl)) { $downloadUri } else { $FallbackUrl }
        try { Invoke-CurlDownload -Uri $primaryUri -Destination $destination -AssetName $AssetName }
        catch {
            if ([string]::IsNullOrWhiteSpace($FallbackUrl)) { throw }
            Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
            Write-Host "[Gitee] Direct URL unavailable for $AssetName; trying the saved attachment ID."
            Invoke-CurlDownload -Uri $downloadUri -Destination $destination -AssetName $AssetName
        }
        return $destination
    } catch {
        Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Test-GiteeAttachment {
    param(
        [Parameter(Mandatory)][hashtable]$Attachment,
        [Parameter(Mandatory)][long]$ReleaseId,
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    $attachmentId = 0L
    if (-not [long]::TryParse([string]$Attachment.id, [ref]$attachmentId) -or $attachmentId -le 0) {
        throw "Gitee returned an invalid attachment ID for $Name."
    }
    if ([string]$Attachment.name -cne $Name) { throw "Gitee attachment ID $attachmentId is named '$($Attachment.name)', expected '$Name'." }
    $localSize = [long](Get-Item -LiteralPath $LocalPath).Length
    if ($null -ne $Attachment.size -and [long]$Attachment.size -ne $localSize) {
        throw "Gitee attachment ID $attachmentId for $Name has a different size; refusing to upload a duplicate."
    }
    $readbackDirectory = Join-Path $stateDirectory ("gitee-readback-check-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $readbackDirectory -Force | Out-Null
    $remotePath = Join-Path $readbackDirectory $Name
    $fallbackUrl = [string]$Attachment.browser_download_url
    if (-not [string]::IsNullOrWhiteSpace($fallbackUrl)) { $fallbackUrl = Assert-SafeGiteeDownloadUrl $fallbackUrl }
    try {
        $downloaded = Get-GiteeAssetReadback -ReleaseId $ReleaseId -AttachmentId ([long]$Attachment.id) -FallbackUrl $fallbackUrl -AssetName $Name
        Move-Item -LiteralPath $downloaded -Destination $remotePath -Force
        Test-DownloadedAsset -RemotePath $remotePath -LocalPath $LocalPath -AssetName $Name -ArchiveSha256 $ArchiveSha256
        $script:verifiedAssets["Gitee/$Name"] = Get-ReleaseFileSha256 $remotePath
        Write-Host "[Gitee] Verified $Name ($localSize bytes) by attachment ID $($Attachment.id)."
        return $fallbackUrl
    } finally {
        Remove-OwnedTemporaryDirectory -Path $readbackDirectory -ExpectedParent $stateDirectory -RequiredNamePrefix 'gitee-readback-check-'
    }
}

function Get-VerifiedGiteeCandidate {
    param(
        [Parameter(Mandatory)][object]$Candidate,
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    $id = 0L
    if (-not [long]::TryParse([string]$Candidate.id, [ref]$id) -or $id -le 0) { throw "Gitee attachment list returned an invalid ID for $Name." }
    $lookup = Get-GiteeAttachmentById -ReleaseId ([long]$script:releaseState.giteeReleaseId) -AttachmentId $id
    $attachment = if ($lookup.Available -and $null -ne $lookup.Attachment) { $lookup.Attachment } else { $Candidate }
    $url = Test-GiteeAttachment -Attachment $attachment -ReleaseId ([long]$script:releaseState.giteeReleaseId) -LocalPath $LocalPath -Name $Name -ArchiveSha256 $ArchiveSha256
    return [pscustomobject]@{ Id = $id; Url = $url }
}

function Find-GiteeAssetForRecovery {
    param(
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    $matches = @(Get-GiteeAttachments | Where-Object { $_.name -ceq $Name })
    if ($matches.Count -gt 1) { throw "Gitee has duplicate attachments named $Name; refusing to select one silently." }
    foreach ($candidate in $matches) {
        $result = Get-VerifiedGiteeCandidate -Candidate $candidate -LocalPath $LocalPath -Name $Name -ArchiveSha256 $ArchiveSha256
        return $result
    }
    return $null
}

function Upload-GiteeAsset {
    param([Parameter(Mandatory)][string]$LocalPath, [Parameter(Mandatory)][string]$Name)

    $curl = (Get-Command curl.exe -ErrorAction Stop).Source
    $responsePath = Join-Path $stateDirectory ("gitee-response-" + [guid]::NewGuid().ToString('N') + '.json')
    $escapedToken = $env:GITEE_TOKEN.Replace('\', '\\').Replace('"', '\"')
    $curlConfig = ('form = "access_token={0}"' -f $escapedToken)
    $asset = Get-Item -LiteralPath $LocalPath
    try {
        Write-Host "[Gitee] Uploading $Name ($($asset.Length) bytes) from the local release cache."
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            $curlOutput = $curlConfig | & $curl --config - --silent --show-error --http1.1 --connect-timeout 20 --max-time 180 --fail-with-body --request POST --form "file=@$($asset.FullName);filename=$Name" --dump-header "$responsePath.headers" --output $responsePath --write-out 'http=%{http_code} seconds=%{time_total}' "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$($script:releaseState.giteeReleaseId)/attach_files" 2>&1
            $stats = $curlOutput | Out-String
            $exitCode = $LASTEXITCODE
            # Only requests rejected before upload can be repeated without a
            # server identity. A timeout/reset/5xx must go through reconciliation.
            $rejected = $stats -match 'http=429\b' -or $exitCode -in @(6, 7)
            if (-not $rejected -or $attempt -eq 4) { break }
            $retryAfter = ''
            if (Test-Path -LiteralPath "$responsePath.headers") {
                $headerText = Get-Content -LiteralPath "$responsePath.headers" -Raw
                if (-not [string]::IsNullOrEmpty($headerText)) {
                    $retryMatch = [regex]::Match($headerText, '(?im)^Retry-After:\s*([^\r\n]+)')
                    if ($retryMatch.Success) { $retryAfter = $retryMatch.Groups[1].Value }
                }
            }
            $delay = Get-ReleaseRetryDelay -Attempt $attempt -RetryAfter $retryAfter
            if ($delay -gt 60) { throw "Gitee upload Retry-After for $Name exceeds this run's retry budget." }
            Write-Host "[Retry] Gitee rejected $Name before upload; waiting $delay seconds ($attempt/4)."
            Start-Sleep -Seconds $delay
        }
        $attachment = $null
        if (Test-Path -LiteralPath $responsePath) {
            try { $attachment = Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop } catch { }
        }
        # A complete response containing an ID can survive a trailing connection
        # error. Save and verify it before considering an attachment-list lookup.
        if ($null -ne $attachment -and [long]$attachment.id -gt 0 -and [string]$attachment.name -ceq $Name) {
            $exitCode = 0
        }
        if ($exitCode -ne 0) {
            # The response may be lost after the server stores the attachment. Recover once by name and hash.
            try {
                $recovered = Find-GiteeAssetForRecovery -LocalPath $LocalPath -Name $Name -ArchiveSha256 $script:releaseState.archiveSha256
                if ($null -ne $recovered) {
                    Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId ([string]$recovered.Id) -DownloadUrl $recovered.Url -Status 'verified'
                    Save-ReleaseState -Path $statePath -State $script:releaseState
                    Write-Host "[Gitee] Upload response was interrupted; attachment ID $($recovered.Id) was recovered and verified."
                    return
                }
            } catch {
                throw "Gitee upload outcome for $Name is uncertain (curl exit $exitCode). Recovery by attachment ID/list failed; no second upload was attempted. $($_.Exception.Message)"
            }
            throw "Gitee upload failed for $Name (curl exit $exitCode); no matching attachment was found, so the workflow stopped without another upload. $($stats.Trim())"
        }

        try { $attachment = Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
        catch { throw "Gitee upload response was invalid for $Name; the next run will inspect attachments before uploading again." }
        $attachmentId = [long]$attachment.id
        if ($attachmentId -le 0 -or [string]$attachment.name -cne $Name) { throw "Gitee upload returned an invalid attachment identity for $Name." }

        $downloadUrl = [string]$attachment.browser_download_url
        Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId ([string]$attachmentId) -DownloadUrl $downloadUrl -Status 'uploaded'
        Save-ReleaseState -Path $statePath -State $script:releaseState
        $verifiedUrl = Test-GiteeAttachment -Attachment $attachment -ReleaseId ([long]$script:releaseState.giteeReleaseId) -LocalPath $LocalPath -Name $Name -ArchiveSha256 $script:releaseState.archiveSha256
        if (-not [string]::IsNullOrWhiteSpace($verifiedUrl)) { $downloadUrl = $verifiedUrl }
        Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId ([string]$attachmentId) -DownloadUrl $downloadUrl -Status 'verified'
        Save-ReleaseState -Path $statePath -State $script:releaseState
        Write-Host "[Gitee] Uploaded and verified $Name (HTTP stats: $($stats.Trim()))."
    } finally {
        Remove-Item -LiteralPath $responsePath, "$responsePath.headers" -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-GiteeAsset {
    param(
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    Assert-ReleaseAssetFileName -LocalPath $LocalPath -Name $Name
    $record = $script:releaseState.giteeAssets[$Name]
    if ($null -ne $record -and -not [string]::IsNullOrWhiteSpace([string]$record.attachmentId)) {
        $id = [long]$record.attachmentId
        $fallback = [string]$record.downloadUrl
        if (-not [string]::IsNullOrWhiteSpace($fallback)) {
            # A saved identity is never discarded because an auxiliary API is
            # unavailable. Hash/size failures remain fatal, even if listing works.
            $attachment = @{ id = $id; name = $Name; browser_download_url = $fallback }
            $url = Test-GiteeAttachment -Attachment $attachment -ReleaseId ([long]$script:releaseState.giteeReleaseId) -LocalPath $LocalPath -Name $Name -ArchiveSha256 $ArchiveSha256
            Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId ([string]$id) -DownloadUrl $url -Status 'verified'
            Save-ReleaseState -Path $statePath -State $script:releaseState
            Write-Host "[Gitee] Reused attachment ID $id for $Name without a list query or upload."
            return
        }
        $lookup = Get-GiteeAttachmentById -ReleaseId ([long]$script:releaseState.giteeReleaseId) -AttachmentId $id
        if ($lookup.Available -and $null -ne $lookup.Attachment) {
            $url = Test-GiteeAttachment -Attachment $lookup.Attachment -ReleaseId ([long]$script:releaseState.giteeReleaseId) -LocalPath $LocalPath -Name $Name -ArchiveSha256 $ArchiveSha256
            Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId ([string]$id) -DownloadUrl $url -Status 'verified'
            Save-ReleaseState -Path $statePath -State $script:releaseState
            return
        }
        throw "Gitee saved attachment $Name (ID $id) cannot be verified; preserving its checkpoint and refusing another upload."
    }

    $mustRecover = ($null -ne $record -and [string]$record.status -eq 'pending') -or
        (-not [bool]$script:releaseState.giteeReleaseCreatedByPipeline)
    if ($mustRecover) {
        # Listing is used only when an earlier upload may have succeeded without a saved ID, or when adopting an existing Release.
        $existing = Find-GiteeAssetForRecovery -LocalPath $LocalPath -Name $Name -ArchiveSha256 $ArchiveSha256
        if ($null -ne $existing) {
            Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId ([string]$existing.Id) -DownloadUrl $existing.Url -Status 'verified'
            Save-ReleaseState -Path $statePath -State $script:releaseState
            return
        }
    }

    Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId '' -DownloadUrl '' -Status 'pending'
    Save-ReleaseState -Path $statePath -State $script:releaseState
    Upload-GiteeAsset -LocalPath $LocalPath -Name $Name
}

function Sync-GiteeSource {
    $giteeUrl = "https://gitee.com/$GiteeOwner/$GiteeRepository.git"
    $askPassScript = Join-Path $stateDirectory ("gitee-askpass-{0}.ps1" -f [guid]::NewGuid().ToString('N'))
    $askPassCommand = Join-Path $stateDirectory ("gitee-askpass-{0}.cmd" -f [guid]::NewGuid().ToString('N'))
    $askPassContent = @'
param([string]$Prompt)
if ($Prompt -match '(?i)username') { [Console]::WriteLine('joel20230302') }
else { [Console]::WriteLine($env:GITEE_TOKEN) }
'@
    Set-Content -LiteralPath $askPassScript -Value $askPassContent -Encoding UTF8
    $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
    $cmdLine = '"{0}" -NoLogo -NoProfile -File "{1}" %*' -f $pwshPath, $askPassScript
    Set-Content -LiteralPath $askPassCommand -Value "@echo off`r`n$cmdLine`r`n" -Encoding ASCII
    $priorAskPass = $env:GIT_ASKPASS
    $priorTerminalPrompt = $env:GIT_TERMINAL_PROMPT
    try {
        $env:GIT_TERMINAL_PROMPT = '0'
        Invoke-GitChecked -Arguments @('fetch', 'origin', 'main', '--tags') | Out-Null
        $mainCommit = (Invoke-GitChecked -Arguments @('rev-parse', 'refs/remotes/origin/main')).Trim()
        $tagCommit = (Invoke-GitChecked -Arguments @('rev-parse', "refs/tags/${Tag}^{commit}")).Trim()
        if ($tagCommit -cne $SourceCommit.ToLowerInvariant()) { throw "Fetched tag $Tag no longer resolves to the validated source commit." }
        & git merge-base --is-ancestor $tagCommit $mainCommit
        if ($LASTEXITCODE -ne 0) { throw "Release tag $Tag is not contained in GitHub main." }

        # Keep the Gitee credential helper scoped to Gitee requests so it cannot be offered to GitHub.
        $env:GIT_ASKPASS = $askPassCommand
        Invoke-GitChecked -Arguments @('fetch', $giteeUrl, '+refs/heads/main:refs/remotes/gitee/main') | Out-Null
        $giteeMain = (Invoke-GitChecked -Arguments @('rev-parse', 'refs/remotes/gitee/main')).Trim()
        & git merge-base --is-ancestor $giteeMain $mainCommit
        if ($LASTEXITCODE -ne 0) { throw 'Gitee main is not an ancestor of GitHub main; refusing to rewrite or overwrite Gitee history.' }
        Invoke-GitChecked -Arguments @('push', $giteeUrl, 'refs/remotes/origin/main:refs/heads/main', "refs/tags/${Tag}:refs/tags/${Tag}") | Out-Null
        Write-Host "[Gitee] Fast-forward synchronized main and tag $Tag."
    } finally {
        if ($null -eq $priorAskPass) { Remove-Item Env:GIT_ASKPASS -ErrorAction SilentlyContinue } else { $env:GIT_ASKPASS = $priorAskPass }
        if ($null -eq $priorTerminalPrompt) { Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue } else { $env:GIT_TERMINAL_PROMPT = $priorTerminalPrompt }
        Remove-Item -LiteralPath $askPassScript, $askPassCommand -Force -ErrorAction SilentlyContinue
    }
}

function Get-OrCreateGitHubRelease {
    $release = Get-GitHubRelease
    if ($null -ne $release) {
        if ($release.isPrerelease) { throw "GitHub Release $Tag exists as a prerelease; refusing to publish it as stable." }
        if ([string]$release.tagName -cne $Tag) { throw "GitHub Release lookup resolved the wrong tag for $Tag." }
        Write-Host "[GitHub] Reusing existing Release $Tag."
        return $release
    }

    $notesPath = Join-Path $stateDirectory 'release-notes.md'
    $notes = [string]$script:baseManifest.releaseNotes
    [System.IO.File]::WriteAllText($notesPath, $notes, [System.Text.UTF8Encoding]::new($false))
    $create = Invoke-Gh -Arguments @('release', 'create', $Tag, '--repo', $GitHubRepository, '--title', "NetBoot DHCP Tool $Tag", '--notes-file', $notesPath, '--draft', '--verify-tag') -AllowFailure
    if ($create.ExitCode -ne 0) {
        # A lost response may still have created the release. Look it up once before deciding the operation failed.
        $release = Get-GitHubRelease
        if ($null -eq $release) { throw "Could not create GitHub Release $Tag. $($create.Output)" }
        Write-Host "[GitHub] Release-create response was interrupted; existing Release $Tag was found and will be reused."
    } else {
        $release = Get-GitHubRelease
        if ($null -eq $release) { throw "GitHub Release $Tag was created but could not be read back." }
        Write-Host "[GitHub] Created draft Release $Tag."
    }
    if ($release.isPrerelease) { throw "GitHub Release $Tag is a prerelease." }
    return $release
}

function Publish-GiteeRelease([System.Collections.IDictionary]$Release) {
    if (-not [bool]$Release.prerelease) { return $Release }
    $releaseId = [long]$Release.id
    $payload = New-GiteeReleasePayload -Operation Publish -Tag $Tag -Name ([string]$Release.name) -Body ([string]$Release.body)
    try {
        $response = Invoke-GiteeJsonRequest -Uri "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$releaseId" -Method Patch -Payload $payload
    } catch { throw 'Could not mark the fully verified Gitee Release as stable.' }
    if ($response.StatusCode -notin @(200, 201)) {
        $errorSummary = Get-GiteeResponseErrorSummary $response
        throw "Gitee stable-release update returned HTTP $($response.StatusCode)$errorSummary."
    }
    try { $verified = $response.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { $verified = Get-GiteeReleaseByTag }
    if ($null -eq $verified -or [long]$verified.id -ne $releaseId -or [bool]$verified.prerelease) {
        throw 'Gitee Release stable-state readback failed.'
    }
    Write-Host "[Gitee] Published stable Release $Tag after manifest verification."
    return $verified
}

function Publish-GitHubRelease {
    $release = Get-GitHubRelease
    if ($null -eq $release) { throw "GitHub Release $Tag is missing before publication." }
    if ($release.isPrerelease) { throw "GitHub Release $Tag is a prerelease." }
    if (-not $release.isDraft) { return $release }
    if ($release.isDraft) {
        $result = Invoke-Gh -Arguments @('release', 'edit', $Tag, '--repo', $GitHubRepository, '--draft=false')
        Write-Host "[GitHub] Published stable Release $Tag after manifest verification."
    }
    $verified = Get-GitHubRelease
    if ($null -eq $verified -or $verified.isDraft -or $verified.isPrerelease) { throw 'GitHub Release stable-state readback failed.' }
    return $verified
}

function Get-GiteeReleasePageUrl {
    return "https://gitee.com/$GiteeOwner/$GiteeRepository/releases/tag/$([uri]::EscapeDataString($Tag))"
}

function Get-GiteeArchiveDownloadUrl {
    return (Get-GiteeAssetDownloadUrl -Name $expectedArchiveName -LocalPath $bundle.ArchivePath -Sha256 $bundle.ArchiveSha256)
}

function Get-GiteeAssetDownloadUrl([string]$Name, [string]$LocalPath, [string]$Sha256) {
    $record = $script:releaseState.giteeAssets[$Name]
    if ($null -eq $record -or [long]$record.attachmentId -le 0) { throw "Gitee asset identity was not recorded after verification: $Name." }
    $uri = "https://gitee.com/$GiteeOwner/$GiteeRepository/attach_files/$($record.attachmentId)/download"
    $temporary = Join-Path $stateDirectory ('gitee-client-url-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-ReleaseDownload -Uri $uri -Destination $temporary -AssetName $Name
        Test-DownloadedAsset -RemotePath $temporary -LocalPath $LocalPath -AssetName $Name -ArchiveSha256 $bundle.ArchiveSha256
        if ((Get-ReleaseFileSha256 $temporary) -cne $Sha256) { throw "Gitee client URL returned different bytes for $Name." }
    } finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    return $uri
}

function Test-ClientManifest([switch]$Live) {
    $dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1')
    $arguments = @('run', '--project', (Join-Path $PSScriptRoot 'release-pipeline/ClientManifestCheck.csproj'), '-c', 'Release', '--', $finalManifestPath, $finalSignaturePath, $version)
    if ($Live) { $arguments += '--live' }
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'The actual published-tag client rejected the manifest or live update source.' }
}

function Get-OrCreateFinalSignedManifest([System.Collections.IDictionary]$GiteeAssetUrls) {
    $candidatePath = Join-Path $finalManifestDirectory ('candidate-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $null = New-DualSourceManifest -BaseManifestPath $bundle.ManifestPath -Tag $Tag -GitHubRepository $GitHubRepository `
            -GiteeReleasePageUrl $giteeReleasePageUrl -GiteeArchiveDownloadUrl $giteeArchiveUrl `
            -GiteeAssetUrls $GiteeAssetUrls -OutputPath $candidatePath
        $manifestExists = Test-Path -LiteralPath $finalManifestPath -PathType Leaf
        $signatureExists = Test-Path -LiteralPath $finalSignaturePath -PathType Leaf
        if ($manifestExists -xor $signatureExists) { throw 'Cached final manifest/signature pair is incomplete; refusing to generate replacement bytes.' }
        if ($manifestExists) {
            if ((Get-ReleaseFileSha256 $candidatePath) -cne (Get-ReleaseFileSha256 $finalManifestPath)) {
                throw 'Cached final manifest does not match the verified release assets; refusing to replace immutable signed bytes.'
            }
            $cachedBytes = [System.IO.File]::ReadAllBytes($finalManifestPath)
            $cachedSignature = [System.IO.File]::ReadAllText($finalSignaturePath).Trim()
            $signatureBytes = [Convert]::FromBase64String($cachedSignature)
            if (-not $script:updateSigningRsa.VerifyData($cachedBytes, $signatureBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)) {
                throw 'Cached detached signature does not match latest.json.'
            }
        } else {
            Copy-Item -LiteralPath $candidatePath -Destination $finalManifestPath
            $manifestBytes = [System.IO.File]::ReadAllBytes($finalManifestPath)
            $signatureBytes = $script:updateSigningRsa.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
            [System.IO.File]::WriteAllText($finalSignaturePath, [Convert]::ToBase64String($signatureBytes), [System.Text.UTF8Encoding]::new($false))
        }
    } finally { Remove-Item -LiteralPath $candidatePath -Force -ErrorAction SilentlyContinue }
}

function Repair-ClientManifest {
    if (-not $RepairManifestSha256) { return }
    $oldHash = $RepairManifestSha256.ToLowerInvariant()
    $newHash = Get-ReleaseFileSha256 $finalManifestPath
    $journal = $script:releaseState.manifestRepair
    if ($null -ne $journal -and ($journal.oldSha256 -cne $oldHash -or $journal.newSha256 -cne $newHash)) {
        throw 'Manifest repair journal disagrees with the reviewed old/new content.'
    }
    if ($null -eq $journal) {
        $journal = @{oldSha256=$oldHash;newSha256=$newHash;mirrors=@{}}
        $script:releaseState.manifestRepair = $journal
    }
    $backupDirectory = Join-Path $stateDirectory "manifest-repair-$oldHash"
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    $candidates = @{}
    foreach ($mirror in @('GitHub', 'Gitee')) {
        if ($mirror -ceq 'GitHub') { $asset = Get-GitHubAsset 'latest.json' }
        else {
            $assets = @(Get-GiteeAttachments | Where-Object { $_.name -ceq 'latest.json' })
            if ($assets.Count -gt 1) { throw 'Cannot repair duplicate Gitee manifests.' }
            $asset = if ($assets.Count -eq 1) { $assets[0] } else { $null }
        }
        if ($null -eq $asset) {
            if ($journal.mirrors[$mirror].status -notin @('delete-pending','deleted')) { throw "$mirror manifest missing without a prior reviewed repair intent." }
            $candidates[$mirror] = @{absent=$true}
            continue
        }
        $readbackDirectory = Join-Path $backupDirectory $mirror
        New-Item -ItemType Directory -Path $readbackDirectory -Force | Out-Null
        $path = Join-Path $readbackDirectory 'latest.json'
        if ($mirror -ceq 'GitHub') { $path = Download-GitHubAsset 'latest.json' $readbackDirectory }
        else { Invoke-ReleaseDownload -Uri ([string]$asset.browser_download_url) -Destination $path -AssetName 'latest.json repair preflight' }
        if ((Get-ReleaseFileSha256 $path) -ceq $newHash) {
            $candidates[$mirror] = @{complete=$true;asset=$asset}
            continue
        }
        Assert-ManifestUrlRepair -OldPath $path -NewPath $finalManifestPath -ExpectedOldSha256 $oldHash -Tag $Tag -GiteeOwner $GiteeOwner -GiteeRepository $GiteeRepository -ArchiveAttachmentId ([long]$script:releaseState.giteeAssets[$expectedArchiveName].attachmentId)
        $original = Join-Path $backupDirectory "$mirror-original-latest.json"
        if (-not (Test-Path -LiteralPath $original)) { Copy-Item -LiteralPath $path -Destination $original }
        $candidates[$mirror] = @{asset=$asset}
    }
    # Both old/new manifests have been checked before deleting either old asset.
    # This explicit, hash-bound exception applies only to latest.json, never to
    # an archive, sidecar, tag or entire Release.
    foreach ($mirror in @('GitHub','Gitee')) {
        $candidate = $candidates[$mirror]
        if ($candidate.complete) {
            if ($mirror -ceq 'Gitee') {
                Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name 'latest.json' -AttachmentId ([string]$candidate.asset.id) -DownloadUrl ([string]$candidate.asset.browser_download_url) -Status uploaded
            }
            $journal.mirrors[$mirror] = @{status='complete'}
            Save-ReleaseState -Path $statePath -State $script:releaseState
            continue
        }
        if (-not $candidate.absent) {
            $asset = $candidate.asset
            $identity = if ($mirror -ceq 'GitHub') { [string]$asset.apiUrl } else { [string]$asset.id }
            $journal.mirrors[$mirror] = @{status='delete-pending';identity=$identity}
            Save-ReleaseState -Path $statePath -State $script:releaseState
            if ($mirror -ceq 'GitHub') {
                $null = Invoke-Gh -Arguments @('api', '--method', 'DELETE', $identity) -AllowFailure
                if ($null -ne (Get-GitHubAsset 'latest.json')) { throw 'GitHub old manifest deletion was not confirmed; refusing duplicate upload.' }
            } else {
                try { $null = Invoke-ReleaseHttp -Uri "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$($script:releaseState.giteeReleaseId)/attach_files/$identity" -Method Delete -Headers (Get-GiteeHeaders) }
                catch { Write-Host '[Manifest repair] Delete response interrupted; reconciling the exact asset before continuing.' }
                if (@(Get-GiteeAttachments | Where-Object { $_.name -ceq 'latest.json' }).Count -ne 0) { throw 'Gitee old manifest deletion was not confirmed; refusing duplicate upload.' }
            }
        }
        $journal.mirrors[$mirror].status = 'deleted'
        if ($mirror -ceq 'Gitee') {
            Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name 'latest.json' -AttachmentId '' -DownloadUrl '' -Status pending
        }
        Save-ReleaseState -Path $statePath -State $script:releaseState
        Write-Host "[Manifest repair] $mirror reviewed old manifest removed; archive/sidecar assets retained."
        if ($mirror -ceq 'GitHub') { Ensure-GitHubAsset -LocalPath $finalManifestPath -Name 'latest.json' -ArchiveSha256 $bundle.ArchiveSha256 }
        else { Ensure-GiteeAsset -LocalPath $finalManifestPath -Name 'latest.json' -ArchiveSha256 $bundle.ArchiveSha256 }
        $journal.mirrors[$mirror].status = 'complete'
        Save-ReleaseState -Path $statePath -State $script:releaseState
    }
}

function Get-ValidatedGitSha([string]$Ref) {
    return (Invoke-GitChecked -Arguments @('rev-parse', $Ref)).Trim().ToLowerInvariant()
}

function Assert-VerifiedMirror([string]$Mirror) {
    foreach ($path in @($bundle.AssetFiles)) {
        $name = Split-Path -Leaf $path
        if ($script:verifiedAssets["$Mirror/$name"] -cne (Get-ReleaseFileSha256 $path)) {
            throw "$Mirror has no complete download and SHA-256 proof for $name in this run."
        }
    }
    Write-Host "[$Mirror] Archive and checksum have complete size/SHA-256 readback evidence from this run."
}

function Assert-ArchiveContents([string]$Path) {
    $sevenZip = 'C:\Program Files\7-Zip\7z.exe'
    $testOutput = & $sevenZip t $Path 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Release archive integrity test failed: $testOutput" }
    $listing = & $sevenZip l -slt $Path 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect release archive contents.' }
    # 7-Zip's technical listing starts with a Path record for the archive
    # itself. Its rendered path can differ across versions, so discard the
    # first Path record instead of comparing it with the caller's path.
    $pathMatches = @([regex]::Matches($listing, '(?m)^Path = ([^\r\n]+)'))
    $files = @($pathMatches | Select-Object -Skip 1 | ForEach-Object { $_.Groups[1].Value.Trim().Replace('/', '\').TrimEnd('\') })
    $rootPrefix = 'NetBootDhcpTool\'
    $outsideEntries = @($files | Where-Object { $_ -cne 'NetBootDhcpTool' -and $_ -notlike "$rootPrefix*" })
    if ($outsideEntries.Count -gt 0) { throw "Portable archive contains entries outside the fixed NetBootDhcpTool top-level directory: $($outsideEntries -join ', ')." }
    foreach ($required in @('NetBootDhcpTool\NetBootDhcpTool.exe', 'NetBootDhcpTool\NetBootDhcpTool.Updater.exe', 'NetBootDhcpTool\install-manifest.json', 'NetBootDhcpTool\config\appsettings.json', 'NetBootDhcpTool\config\favorites.json', 'NetBootDhcpTool\i18n\zh-CN.json', 'NetBootDhcpTool\i18n\en-US.json', 'NetBootDhcpTool\assets\app.ico', 'NetBootDhcpTool\README.md', 'NetBootDhcpTool\docs\RELEASE_NOTES.md', 'NetBootDhcpTool\PresentationNative_cor3.dll', 'NetBootDhcpTool\wpfgfx_cor3.dll')) {
        if ($required -cnotin $files) { throw "Release archive is incomplete; missing $required." }
    }
    Write-Host '[Build] Archive CRC and required application files verified.'
}

function Assert-PublicRelease {
    $directory = Join-Path $stateDirectory ('public-readback-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    try {
        $publicAssets = @($bundle.AssetFiles) + @($finalManifestPath, $finalSignaturePath)
        foreach ($mirror in @('GitHub', 'Gitee')) {
            foreach ($path in $publicAssets) {
                $name = Split-Path -Leaf $path
                $uri = if ($mirror -ceq 'GitHub') { "https://github.com/$GitHubRepository/releases/download/$Tag/$name" } else { [string]$script:releaseState.giteeAssets[$name].downloadUrl }
                $download = Join-Path $directory "$mirror-$name"
                Invoke-ReleaseDownload -Uri $uri -Destination $download -AssetName $name
                Test-DownloadedAsset -RemotePath $download -LocalPath $path -AssetName $name -ArchiveSha256 $bundle.ArchiveSha256
                Write-Host "[Final Verify] Anonymous $mirror $name download: size and SHA-256 match."
            }
        }
        $latestPath = Join-Path $directory 'github-latest.json'
        Invoke-ReleaseDownload -Uri "https://github.com/$GitHubRepository/releases/latest/download/latest.json" -Destination $latestPath -AssetName 'latest.json (client endpoint)'
        Test-DownloadedAsset -RemotePath $latestPath -LocalPath $finalManifestPath -AssetName 'latest.json' -ArchiveSha256 $bundle.ArchiveSha256
        $latestSignaturePath = Join-Path $directory 'github-latest.json.sig'
        Invoke-ReleaseDownload -Uri "https://github.com/$GitHubRepository/releases/latest/download/latest.json.sig" -Destination $latestSignaturePath -AssetName 'latest.json.sig (client endpoint)'
        Test-DownloadedAsset -RemotePath $latestSignaturePath -LocalPath $finalSignaturePath -AssetName 'latest.json.sig' -ArchiveSha256 $bundle.ArchiveSha256
        $latest = Invoke-ReleaseHttp -Uri "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/latest"
        if ($latest.StatusCode -ne 200) { throw "Gitee client latest-release endpoint returned HTTP $($latest.StatusCode)." }
        $latestRelease = $latest.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        if ($latestRelease.tag_name -cne $Tag -or [bool]$latestRelease.prerelease -or [long]$latestRelease.id -ne [long]$script:releaseState.giteeReleaseId) {
            throw 'Gitee client latest-release endpoint points to the wrong version or Release.'
        }
        # Only this auxiliary inventory request can be downgraded, and only after
        # both complete public bundles and the client latest endpoints passed.
        $attachments = $null
        try { $attachments = @(Get-GiteeAttachments) }
        catch { Add-ReleaseWarning 'Gitee attachment inventory unavailable after both public bundles passed full download/size/SHA-256 verification; saved attachment IDs were retained.' }
        if ($null -ne $attachments) {
            $expectedNames = @($publicAssets | ForEach-Object { Split-Path -Leaf $_ })
            foreach ($name in $expectedNames) {
                $matching = @($attachments | Where-Object { $_.name -ceq $name })
                if ($matching.Count -ne 1) { throw "Gitee inventory has $($matching.Count) attachments named $name; expected exactly one." }
                if ([string]$matching[0].id -cne [string]$script:releaseState.giteeAssets[$name].attachmentId -or
                    [string]$matching[0].browser_download_url -cne [string]$script:releaseState.giteeAssets[$name].downloadUrl) {
                    throw "Gitee inventory for $name disagrees with its verified attachment identity/URL."
                }
            }
            Write-Host '[Final Verify] Gitee inventory: exactly one of each archive, Full/OTA, sidecar and signed-manifest asset; client URLs match verified bytes.'
        }
    } finally {
        Remove-OwnedTemporaryDirectory -Path $directory -ExpectedParent $stateDirectory -RequiredNamePrefix 'public-readback-'
    }
}

try {
Start-ReleaseStage 'Build'
Set-Location $repoRoot
$headCommit = Get-ValidatedGitSha 'HEAD'
if ($headCommit -cne $SourceCommit.ToLowerInvariant()) { throw "Checked-out HEAD $headCommit does not match validated CI commit $SourceCommit." }

$archiveName = $expectedArchiveName
$cacheIsPresent = Test-Path -LiteralPath $stateDirectory -PathType Container
if (-not $cacheIsPresent) {
    $existingGitHubRelease = Get-GitHubRelease
    $existingGiteeRelease = Get-GiteeReleaseByTag
    Assert-ReleaseCachePolicy -CachePresent $false -GitHubReleaseExists ($null -ne $existingGitHubRelease) -GiteeReleaseExists ($null -ne $existingGiteeRelease) -Tag $Tag
}
if ($cacheIsPresent) {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw "Incomplete release cache for $Tag at $stateDirectory; refusing to rebuild or upload." }
    $bundle = Get-ReleaseBundle -Directory $stateDirectory -Tag $Tag
    $script:releaseState = Read-ReleaseState -Path $statePath -Tag $Tag -SourceCommit $SourceCommit -ArchiveSha256 $bundle.ArchiveSha256
    Write-Host "[Package] Reusing cached $archiveName; it will not be rebuilt on workflow retry."
} else {
    $stagingDirectory = "$stateDirectory.$([guid]::NewGuid().ToString('N')).tmp"
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    try {
        $buildOutput = Join-Path $stagingDirectory 'build-output'
        Write-Host "[Package] Building Full/OTA and portable archive for $Tag in an isolated cache directory."
        & (Join-Path $PSScriptRoot 'publish.ps1') -GitHubRepository $GitHubRepository -OutputDirectory $buildOutput -BaselineDirectory $fullStateRoot
        if ($LASTEXITCODE -ne 0) { throw "Release package build failed with exit code $LASTEXITCODE." }
        $builtBundle = Get-ReleaseBundle -Directory $buildOutput -Tag $Tag
        foreach ($assetPath in $builtBundle.AssetFiles) { Copy-Item -LiteralPath $assetPath -Destination $stagingDirectory }
        Copy-Item -LiteralPath $builtBundle.ManifestPath -Destination (Join-Path $stagingDirectory 'latest.json')
        Copy-Item -LiteralPath (Join-Path $buildOutput 'NetBootDhcpTool\install-manifest.json') -Destination (Join-Path $stagingDirectory 'install-manifest.json')
        $script:releaseState = New-ReleaseState -Tag $Tag -SourceCommit $SourceCommit -ArchiveSha256 $builtBundle.ArchiveSha256
        $script:releaseState.giteeReleaseCreatedByPipeline = $false
        Save-ReleaseState -Path (Join-Path $stagingDirectory 'release-state.json') -State $script:releaseState
        Move-Item -LiteralPath $stagingDirectory -Destination $stateDirectory
    } finally {
        Remove-OwnedTemporaryDirectory -Path $stagingDirectory -ExpectedParent (Split-Path -Parent $stateDirectory) -RequiredNamePrefix "$Tag."
    }
    $bundle = Get-ReleaseBundle -Directory $stateDirectory -Tag $Tag
    Write-Host "[Package] Cached $archiveName ($($bundle.ArchiveSize) bytes, SHA-256 $($bundle.ArchiveSha256))."
}

$script:bundle = $bundle
$script:baseManifest = $bundle.Manifest
if ($bundle.Version -cne $version -or $bundle.ArchiveName -cne $archiveName) { throw 'Cached release bundle does not match this workflow input.' }
Assert-ArchiveContents $bundle.ArchivePath

# Create local notes before creating either public Release; the note source remains the project change log.
$script:releaseState = Read-ReleaseState -Path $statePath -Tag $Tag -SourceCommit $SourceCommit -ArchiveSha256 $bundle.ArchiveSha256
Start-ReleaseStage 'GitHub Publish'
$null = Get-OrCreateGitHubRelease

# Upload the exact same cached archive and sidecar to both hosts. Reuse is allowed only after full remote readback.
Ensure-GitHubAsset -LocalPath $bundle.ArchivePath -Name $bundle.ArchiveName -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GitHubAsset -LocalPath $bundle.ChecksumPath -Name "$($bundle.ArchiveName).sha256" -ArchiveSha256 $bundle.ArchiveSha256
foreach ($packageAsset in $bundle.PackageAssets) {
    Ensure-GitHubAsset -LocalPath $packageAsset.Path -Name $packageAsset.Name -ArchiveSha256 $bundle.ArchiveSha256
    Ensure-GitHubAsset -LocalPath $packageAsset.ChecksumPath -Name "$($packageAsset.Name).sha256" -ArchiveSha256 $bundle.ArchiveSha256
}
Start-ReleaseStage 'GitHub Verify'
Assert-VerifiedMirror 'GitHub'
Start-ReleaseStage 'Gitee Publish'
Sync-GiteeSource
$giteeRelease = Ensure-GiteeRelease -BaseManifest $script:baseManifest
Ensure-GiteeAsset -LocalPath $bundle.ChecksumPath -Name "$($bundle.ArchiveName).sha256" -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GiteeAsset -LocalPath $bundle.ArchivePath -Name $bundle.ArchiveName -ArchiveSha256 $bundle.ArchiveSha256
foreach ($packageAsset in $bundle.PackageAssets) {
    Ensure-GiteeAsset -LocalPath $packageAsset.ChecksumPath -Name "$($packageAsset.Name).sha256" -ArchiveSha256 $bundle.ArchiveSha256
    Ensure-GiteeAsset -LocalPath $packageAsset.Path -Name $packageAsset.Name -ArchiveSha256 $bundle.ArchiveSha256
}
Start-ReleaseStage 'Gitee Verify'
Assert-VerifiedMirror 'Gitee'

# latest.json is created only after both remote archive and checksum pairs pass full readback.
Start-ReleaseStage 'Manifest Publish'
$giteeArchiveUrl = Get-GiteeArchiveDownloadUrl
$giteeReleasePageUrl = Get-GiteeReleasePageUrl
$finalManifestDirectory = Join-Path $stateDirectory 'final-manifest'
New-Item -ItemType Directory -Path $finalManifestDirectory -Force | Out-Null
$finalManifestPath = Join-Path $finalManifestDirectory 'latest.json'
$finalSignaturePath = Join-Path $finalManifestDirectory 'latest.json.sig'
$giteeAssetUrls = @{}
foreach ($packageAsset in $bundle.PackageAssets) {
    $giteeAssetUrls[$packageAsset.Name] = Get-GiteeAssetDownloadUrl $packageAsset.Name $packageAsset.Path $packageAsset.Sha256
}
Get-OrCreateFinalSignedManifest -GiteeAssetUrls $giteeAssetUrls
$finalManifestBundleHash = Get-ReleaseFileSha256 $finalManifestPath
Test-ClientManifest
Repair-ClientManifest

Ensure-GiteeAsset -LocalPath $finalSignaturePath -Name 'latest.json.sig' -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GitHubAsset -LocalPath $finalSignaturePath -Name 'latest.json.sig' -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GiteeAsset -LocalPath $finalManifestPath -Name 'latest.json' -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GitHubAsset -LocalPath $finalManifestPath -Name 'latest.json' -ArchiveSha256 $bundle.ArchiveSha256

$finalGiteeManifest = $script:releaseState.giteeAssets['latest.json']
if ($null -eq $finalGiteeManifest -or $finalGiteeManifest.status -ne 'verified') { throw 'Gitee latest.json did not reach the verified state.' }
foreach ($mirror in @('GitHub', 'Gitee')) {
    if ($script:verifiedAssets["$mirror/latest.json"] -cne $finalManifestBundleHash -or
        $script:verifiedAssets["$mirror/latest.json.sig"] -cne (Get-ReleaseFileSha256 $finalSignaturePath)) {
        throw "$mirror signed manifest pair lacks matching full readback evidence from this run."
    }
}

Start-ReleaseStage 'Final Verify'
$githubRelease = Publish-GitHubRelease
$giteeRelease = Publish-GiteeRelease -Release $giteeRelease
if ([bool]$giteeRelease.prerelease -or $githubRelease.isDraft -or $githubRelease.isPrerelease) {
    throw "One or both $Tag Releases did not reach the stable published state."
}
Assert-PublicRelease
Test-ClientManifest -Live

Write-Host "Formal release completed and remotely verified: $Tag"
Write-Host "Archive SHA-256: $($bundle.ArchiveSha256)"
Write-Host "Gitee archive: $giteeArchiveUrl"
Write-Host "GitHub archive: https://github.com/$GitHubRepository/releases/download/$Tag/$archiveName"
Write-Host 'GitHub and Gitee latest.json contain the same verified dual-source manifest.'
Write-ReleaseSummary -Succeeded $true
# Actions propagates LASTEXITCODE after pwsh exits. Only reset a handled native
# failure after every mandatory stage passed, so an auxiliary fallback cannot
# turn a fully verified release red; exceptions above still fail the workflow.
$global:LASTEXITCODE = 0
} catch {
    Write-ReleaseSummary -Succeeded $false
    throw
}
