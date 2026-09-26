[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$StateRoot,
    [string]$GitHubRepository = $env:GITHUB_REPOSITORY,
    [string]$GiteeOwner = 'joel20230302',
    [string]$GiteeRepository = 'NetBootDhcpTool'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'release-pipeline\ReleaseState.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot
$version = Get-ReleaseVersionFromTag $Tag
$expectedArchiveName = Get-ReleaseArchiveName $Tag
$projectPath = Join-Path $repoRoot 'src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath
$projectVersion = [string]$project.Project.PropertyGroup.Version
if ($projectVersion -cne $version) { throw "Tag $Tag does not match application version $projectVersion." }
if ($SourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'SourceCommit must be a full Git SHA.' }
if ([string]::IsNullOrWhiteSpace($GitHubRepository) -or $GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'GitHubRepository must be in owner/repository form.'
}
if ($GiteeOwner -notmatch '^[A-Za-z0-9_.-]+$' -or $GiteeRepository -notmatch '^[A-Za-z0-9_.-]+$') {
    throw 'Gitee owner and repository must contain only letters, digits, dots, underscores, or hyphens.'
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN is not available to the release job.' }
if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) { throw 'GITEE_TOKEN is not available to the release job.' }
if ($env:GITEE_TOKEN -match '[\r\n]') { throw 'GITEE_TOKEN contains a line break and cannot be passed safely.' }

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
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Invoke-GitChecked {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & git @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
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

    $output = & gh @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "GitHub CLI command failed (exit $exitCode): $($output.Trim())"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output.Trim() }
}

function Get-GitHubRelease {
    $result = Invoke-Gh -Arguments @('release', 'view', $Tag, '--repo', $GitHubRepository, '--json', 'tagName,name,body,isDraft,isPrerelease,assets') -AllowFailure
    if ($result.ExitCode -ne 0) {
        if ($result.Output -match '(?i)\brelease not found\b') { return $null }
        throw "GitHub Release lookup failed for ${Tag}: $($result.Output)"
    }
    try { return $result.Output | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { throw "GitHub Release lookup returned invalid JSON for $Tag." }
}

function Get-GitHubAsset([string]$Name) {
    $release = Get-GitHubRelease
    if ($null -eq $release) { return $null }
    return @($release.assets | Where-Object { $_.name -ceq $Name } | Select-Object -First 1)[0]
}

function Test-DownloadedAsset {
    param(
        [Parameter(Mandatory)][string]$RemotePath,
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    if (-not (Test-Path -LiteralPath $RemotePath -PathType Leaf)) { throw "Remote readback did not create $AssetName." }
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
    $result = Invoke-Gh -Arguments @('release', 'download', $Tag, '--repo', $GitHubRepository, '--pattern', $Name, '--dir', $DestinationDirectory)
    $path = Join-Path $DestinationDirectory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "GitHub download did not create $Name." }
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
        $upload = Invoke-Gh -Arguments @('release', 'upload', $Tag, $LocalPath, '--repo', $GitHubRepository) -AllowFailure
        if ($upload.ExitCode -ne 0) {
            # The server may have stored the asset even when the upload response was lost.
            $asset = Get-GitHubAsset $Name
            if ($null -eq $asset) { throw "GitHub upload failed for $Name; no matching asset is visible. $($upload.Output)" }
            Write-Host "[GitHub] Upload response failed, but $Name is present; verifying it before continuing."
            if ([long]$asset.size -ne [long](Get-Item -LiteralPath $LocalPath).Length) {
                throw "GitHub upload outcome is ambiguous and $Name has a different size; refusing another upload."
            }
        }
    }

    $readbackDirectory = Join-Path $stateDirectory ("github-readback-" + [guid]::NewGuid().ToString('N'))
    try {
        $remotePath = Download-GitHubAsset $Name $readbackDirectory
        Test-DownloadedAsset -RemotePath $remotePath -LocalPath $LocalPath -AssetName $Name -ArchiveSha256 $ArchiveSha256
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
    return Invoke-WebRequest -Uri (Assert-SafeGiteeDownloadUrl $Uri) -Method $Method -Headers (Get-GiteeHeaders) `
        -ContentType 'application/json; charset=utf-8' -Body $jsonBody -SkipHttpErrorCheck -TimeoutSec 30
}

function Get-GiteeReleaseByTag {
    $encodedTag = [uri]::EscapeDataString($Tag)
    $uri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/tags/$encodedTag"
    try { $response = Invoke-WebRequest -Uri (Assert-SafeGiteeDownloadUrl $uri) -Method Get -Headers (Get-GiteeHeaders) -SkipHttpErrorCheck -TimeoutSec 30 }
    catch { throw 'Gitee Release lookup failed before any asset upload.' }
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
        } catch { throw 'Gitee Release creation failed; rerunning this tag is safe.' }
        if ($created.StatusCode -notin @(200, 201)) {
            $errorSummary = Get-GiteeResponseErrorSummary $created
            throw "Gitee Release creation returned HTTP $($created.StatusCode)$errorSummary."
        }
        try { $release = $created.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
        catch { throw 'Gitee Release creation returned invalid JSON.' }
        $createdByPipeline = $true
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
        $release = Get-GiteeReleaseByTag
        if ($null -eq $release -or [long]$release.id -ne $releaseId) { throw 'Gitee Release metadata readback did not resolve to the same Release ID.' }
    }
    return $release
}

function Get-GiteeAttachmentById {
    param([Parameter(Mandatory)][long]$ReleaseId, [Parameter(Mandatory)][long]$AttachmentId)

    $uri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$ReleaseId/attach_files/$AttachmentId"
    try { $response = Invoke-WebRequest -Uri $uri -Method Get -Headers (Get-GiteeHeaders) -SkipHttpErrorCheck -TimeoutSec 30 }
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
    $uri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$($script:releaseState.giteeReleaseId)/attach_files?page=1&per_page=100"
    try { $response = Invoke-WebRequest -Uri $uri -Method Get -Headers (Get-GiteeHeaders) -SkipHttpErrorCheck -TimeoutSec 60 }
    catch { throw 'Gitee attachment-list recovery request failed.' }
    if ($response.StatusCode -ne 200) { throw "Gitee attachment-list recovery returned HTTP $($response.StatusCode)." }
    try { return @($response.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop) }
    catch { throw 'Gitee attachment-list recovery returned invalid JSON.' }
}

function Invoke-CurlDownload {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$AssetName
    )

    $safeUri = Assert-SafeGiteeDownloadUrl $Uri
    $curl = (Get-Command curl.exe -ErrorAction Stop).Source
    $stats = & $curl --silent --show-error --location --connect-timeout 20 --max-time 600 --fail-with-body --output $Destination --write-out 'http=%{http_code} bytes=%{size_download}' $safeUri 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Gitee download failed for $AssetName (curl exit $exitCode): $($stats.Trim())" }
    if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) { throw "Gitee download did not create $AssetName." }
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
        try { Invoke-CurlDownload -Uri $downloadUri -Destination $destination -AssetName $AssetName }
        catch {
            if ([string]::IsNullOrWhiteSpace($FallbackUrl)) { throw }
            Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
            Write-Host "[Gitee] Single-attachment download endpoint was unavailable for $AssetName; checking its returned direct URL."
            Invoke-CurlDownload -Uri $FallbackUrl -Destination $destination -AssetName $AssetName
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
        $curlOutput = $curlConfig | & $curl --config - --silent --show-error --http1.1 --connect-timeout 20 --max-time 600 --fail-with-body --request POST --form "file=@$($asset.FullName);filename=$Name" --output $responsePath --write-out 'http=%{http_code} seconds=%{time_total}' "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$($script:releaseState.giteeReleaseId)/attach_files" 2>&1
        $stats = $curlOutput | Out-String
        $exitCode = $LASTEXITCODE
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
        Remove-Item -LiteralPath $responsePath -Force -ErrorAction SilentlyContinue
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
        $lookup = Get-GiteeAttachmentById -ReleaseId ([long]$script:releaseState.giteeReleaseId) -AttachmentId $id
        if ($lookup.Available -and $null -ne $lookup.Attachment) {
            $url = Test-GiteeAttachment -Attachment $lookup.Attachment -ReleaseId ([long]$script:releaseState.giteeReleaseId) -LocalPath $LocalPath -Name $Name -ArchiveSha256 $ArchiveSha256
            Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId ([string]$id) -DownloadUrl $url -Status 'verified'
            Save-ReleaseState -Path $statePath -State $script:releaseState
            return
        }
        $fallback = [string]$record.downloadUrl
        if (-not [string]::IsNullOrWhiteSpace($fallback)) {
            try {
                $readback = Get-GiteeAssetReadback -ReleaseId ([long]$script:releaseState.giteeReleaseId) -AttachmentId $id -FallbackUrl $fallback -AssetName $Name
                try {
                    Test-DownloadedAsset -RemotePath $readback -LocalPath $LocalPath -AssetName $Name -ArchiveSha256 $ArchiveSha256
                    Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId ([string]$id) -DownloadUrl $fallback -Status 'verified'
                    Save-ReleaseState -Path $statePath -State $script:releaseState
                    Write-Host "[Gitee] Verified cached attachment ID $id for $Name without using the attachment list API."
                    return
                } finally { Remove-Item -LiteralPath $readback -Force -ErrorAction SilentlyContinue }
            } catch {
                if (-not $lookup.Available) { Write-Host "[Gitee] Cached attachment ID $id could not be verified directly; checking the recovery list before any new upload." }
            }
        }
        if (-not $lookup.Available -and [string]::IsNullOrWhiteSpace($fallback)) {
            throw "Gitee single-attachment lookup is temporarily unavailable for $Name (ID $id); refusing to upload a duplicate."
        }
        # The known ID is missing or its saved URL no longer serves the expected bytes. Recover before any new upload.
        $script:releaseState.giteeAssets.Remove($Name)
        Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $Name -AttachmentId '' -DownloadUrl '' -Status 'pending'
        Save-ReleaseState -Path $statePath -State $script:releaseState
        $record = $script:releaseState.giteeAssets[$Name]
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
    $verified = Get-GiteeReleaseByTag
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
    $record = $script:releaseState.giteeAssets[$expectedArchiveName]
    if ($null -ne $record -and -not [string]::IsNullOrWhiteSpace([string]$record.downloadUrl)) { return Assert-SafeGiteeDownloadUrl ([string]$record.downloadUrl) }
    $lookup = Get-GiteeAttachmentById -ReleaseId ([long]$script:releaseState.giteeReleaseId) -AttachmentId ([long]$record.attachmentId)
    if ($lookup.Available -and $null -ne $lookup.Attachment) { return Assert-SafeGiteeDownloadUrl ([string]$lookup.Attachment.browser_download_url) }
    throw 'Gitee archive download URL was not recorded after attachment verification.'
}

function Get-ValidatedGitSha([string]$Ref) {
    return (Invoke-GitChecked -Arguments @('rev-parse', $Ref)).Trim().ToLowerInvariant()
}

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
    Write-Host "[Package] Building the single release archive for $Tag."
    & (Join-Path $PSScriptRoot 'publish.ps1') -GitHubRepository $GitHubRepository
    if ($LASTEXITCODE -ne 0) { throw "Release package build failed with exit code $LASTEXITCODE." }
    $builtBundle = Get-ReleaseBundle -Directory (Join-Path $repoRoot 'release') -Tag $Tag
    $stagingDirectory = "$stateDirectory.$([guid]::NewGuid().ToString('N')).tmp"
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    try {
        Copy-Item -LiteralPath $builtBundle.ArchivePath -Destination $stagingDirectory
        Copy-Item -LiteralPath $builtBundle.ChecksumPath -Destination $stagingDirectory
        Copy-Item -LiteralPath $builtBundle.ManifestPath -Destination (Join-Path $stagingDirectory 'latest.json')
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

# Create local notes before creating either public Release; the note source remains the project change log.
$script:releaseState = Read-ReleaseState -Path $statePath -Tag $Tag -SourceCommit $SourceCommit -ArchiveSha256 $bundle.ArchiveSha256
Sync-GiteeSource
$null = Get-OrCreateGitHubRelease
$giteeRelease = Ensure-GiteeRelease -BaseManifest $script:baseManifest

# Upload the exact same cached archive and sidecar to both hosts. Reuse is allowed only after full remote readback.
Ensure-GitHubAsset -LocalPath $bundle.ArchivePath -Name $bundle.ArchiveName -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GitHubAsset -LocalPath $bundle.ChecksumPath -Name "$($bundle.ArchiveName).sha256" -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GiteeAsset -LocalPath $bundle.ChecksumPath -Name "$($bundle.ArchiveName).sha256" -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GiteeAsset -LocalPath $bundle.ArchivePath -Name $bundle.ArchiveName -ArchiveSha256 $bundle.ArchiveSha256

# latest.json is created only after both remote archive and checksum pairs pass full readback.
$giteeArchiveUrl = Get-GiteeArchiveDownloadUrl
$giteeReleasePageUrl = Get-GiteeReleasePageUrl
$finalManifestDirectory = Join-Path $stateDirectory 'final-manifest'
New-Item -ItemType Directory -Path $finalManifestDirectory -Force | Out-Null
$finalManifestPath = Join-Path $finalManifestDirectory 'latest.json'
$finalManifest = New-DualSourceManifest -BaseManifestPath $bundle.ManifestPath -Tag $Tag -GitHubRepository $GitHubRepository -GiteeReleasePageUrl $giteeReleasePageUrl -GiteeArchiveDownloadUrl $giteeArchiveUrl -OutputPath $finalManifestPath
$finalManifestBundleHash = Get-ReleaseFileSha256 $finalManifestPath

Ensure-GiteeAsset -LocalPath $finalManifestPath -Name 'latest.json' -ArchiveSha256 $bundle.ArchiveSha256
Ensure-GitHubAsset -LocalPath $finalManifestPath -Name 'latest.json' -ArchiveSha256 $bundle.ArchiveSha256

$finalGiteeManifest = $script:releaseState.giteeAssets['latest.json']
if ($null -eq $finalGiteeManifest -or $finalGiteeManifest.status -ne 'verified') { throw 'Gitee latest.json did not reach the verified state.' }
$githubManifestDirectory = Join-Path $stateDirectory ("github-manifest-readback-" + [guid]::NewGuid().ToString('N'))
try {
    $githubManifestPath = Download-GitHubAsset 'latest.json' $githubManifestDirectory
    if ((Get-ReleaseFileSha256 $githubManifestPath) -ne $finalManifestBundleHash) { throw 'GitHub latest.json does not match the verified Gitee manifest.' }
} finally {
    Remove-OwnedTemporaryDirectory -Path $githubManifestDirectory -ExpectedParent $stateDirectory -RequiredNamePrefix 'github-manifest-readback-'
}

$githubRelease = Publish-GitHubRelease
$giteeRelease = Publish-GiteeRelease -Release $giteeRelease
if ([bool]$giteeRelease.prerelease -or $githubRelease.isDraft -or $githubRelease.isPrerelease) {
    throw "One or both $Tag Releases did not reach the stable published state."
}

Write-Host "Formal release completed and remotely verified: $Tag"
Write-Host "Archive SHA-256: $($bundle.ArchiveSha256)"
Write-Host "Gitee archive: $giteeArchiveUrl"
Write-Host "GitHub archive: https://github.com/$GitHubRepository/releases/download/$Tag/$archiveName"
Write-Host 'GitHub and Gitee latest.json contain the same verified dual-source manifest.'
