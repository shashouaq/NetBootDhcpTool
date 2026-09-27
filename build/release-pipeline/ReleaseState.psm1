Set-StrictMode -Version Latest

function Get-ReleaseVersionFromTag {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Tag)

    if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+)$') {
        throw "Release tag must use the stable v<major>.<minor>.<patch> format: $Tag"
    }

    return $Matches.version
}

function Get-ReleaseArchiveName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Tag)

    $null = Get-ReleaseVersionFromTag $Tag
    return "NetBootDhcpTool-$Tag.7z"
}

function Test-SuccessfulMainWindowsCiRun {
    [CmdletBinding()]
    param(
        [AllowNull()][object[]]$Runs,
        [Parameter(Mandatory)][string]$Commit
    )

    if ($Commit -notmatch '^[a-fA-F0-9]{40}$') { throw 'CI evidence requires a full Git SHA.' }
    foreach ($run in $Runs) {
        if ($null -ne $run -and
            [string]$run.head_sha -ceq $Commit -and
            [string]$run.event -ceq 'push' -and
            [string]$run.head_branch -ceq 'main' -and
            [string]$run.conclusion -ceq 'success') {
            return $true
        }
    }
    return $false
}

function New-GiteeReleasePayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Create', 'Update', 'Publish')][string]$Operation,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Body,
        [string]$TargetCommit
    )

    $null = Get-ReleaseVersionFromTag $Tag
    $payload = [ordered]@{ tag_name = $Tag; name = $Name; body = $Body }
    if ($Operation -ceq 'Create') {
        if ($TargetCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Gitee Release target must be a full Git SHA.' }
        $payload.target_commitish = $TargetCommit.ToLowerInvariant()
        $payload.prerelease = $true
    } elseif ($Operation -ceq 'Publish') {
        $payload.prerelease = $false
    }
    return $payload
}

function Get-ReleaseFileSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release file does not exist: $Path"
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-SafeGiteeDownloadUrl {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Uri)

    $parsed = $null
    if (-not [uri]::TryCreate($Uri, [System.UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -cne 'https' -or
        ($parsed.Host -cne 'gitee.com' -and -not $parsed.Host.EndsWith('.gitee.com', [System.StringComparison]::OrdinalIgnoreCase)) -or
        -not [string]::IsNullOrEmpty($parsed.UserInfo) -or
        $parsed.Query -match '(?i)(?:^|[&?])(?:access_token|token)=') {
        throw 'Gitee returned a download URL outside the trusted HTTPS Gitee domains.'
    }
    $configuredToken = [System.Environment]::GetEnvironmentVariable('GITEE_TOKEN')
    if (-not [string]::IsNullOrWhiteSpace($configuredToken) -and $Uri.Contains($configuredToken, [System.StringComparison]::Ordinal)) {
        throw 'Gitee returned a download URL containing the configured credential.'
    }
    return $parsed.AbsoluteUri
}

function Get-ReleaseBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Tag
    )

    $version = Get-ReleaseVersionFromTag $Tag
    $archiveName = Get-ReleaseArchiveName $Tag
    $archivePath = Join-Path $Directory $archiveName
    $checksumPath = "$archivePath.sha256"
    $manifestPath = Join-Path $Directory 'latest.json'

    foreach ($path in @($archivePath, $checksumPath, $manifestPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release bundle is incomplete; missing $(Split-Path -Leaf $path)."
        }
    }

    $archiveHash = Get-ReleaseFileSha256 $archivePath
    $sidecarParts = (Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+', 2
    if ($sidecarParts.Count -ne 2 -or $sidecarParts[0] -notmatch '^[a-fA-F0-9]{64}$' -or $sidecarParts[1] -cne $archiveName) {
        throw "Release checksum sidecar is malformed for $archiveName."
    }
    if ($sidecarParts[0].ToLowerInvariant() -ne $archiveHash) {
        throw "Release archive and checksum sidecar disagree for $Tag."
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    } catch {
        throw "Release latest.json is invalid for $Tag."
    }
    if ($manifest.version -ne $version -or
        $manifest.archiveName -ne $archiveName -or
        ([string]$manifest.archiveSha256).ToLowerInvariant() -ne $archiveHash) {
        throw "Release latest.json does not match the local archive for $Tag."
    }

    return [ordered]@{
        Tag = $Tag
        Version = $version
        ArchiveName = $archiveName
        ArchivePath = $archivePath
        ChecksumPath = $checksumPath
        ManifestPath = $manifestPath
        ArchiveSha256 = $archiveHash
        ArchiveSize = [long](Get-Item -LiteralPath $archivePath).Length
        Manifest = $manifest
    }
}

function New-ReleaseState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$SourceCommit,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    $version = Get-ReleaseVersionFromTag $Tag
    if ($SourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Release source commit must be a full Git SHA.' }
    if ($ArchiveSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Release archive SHA-256 is invalid.' }

    return [ordered]@{
        schemaVersion = 1
        tag = $Tag
        version = $version
        sourceCommit = $SourceCommit.ToLowerInvariant()
        archiveSha256 = $ArchiveSha256.ToLowerInvariant()
        giteeReleaseId = $null
        giteeAssets = [ordered]@{}
    }
}

function Save-ReleaseState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Collections.IDictionary]$State
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporaryPath = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    $json = ConvertTo-Json -InputObject $State -Depth 12
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::Move($temporaryPath, $Path, $true)
    } finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Read-ReleaseState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$SourceCommit,
        [Parameter(Mandatory)][string]$ArchiveSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cached release state is missing: $Path"
    }
    try {
        $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    } catch {
        throw 'Cached release state is invalid JSON; refusing to rebuild or overwrite this tag.'
    }

    $expectedVersion = Get-ReleaseVersionFromTag $Tag
    if ($state.schemaVersion -ne 1 -or
        $state.tag -cne $Tag -or
        $state.version -cne $expectedVersion -or
        $state.sourceCommit -cne $SourceCommit.ToLowerInvariant() -or
        $state.archiveSha256 -cne $ArchiveSha256.ToLowerInvariant()) {
        throw "Cached release state does not match $Tag at $SourceCommit; refusing to overwrite it."
    }
    if ($null -eq $state.giteeAssets) { $state.giteeAssets = [ordered]@{} }
    return $state
}

function Assert-ReleaseCachePolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][bool]$CachePresent,
        [Parameter(Mandatory)][bool]$GitHubReleaseExists,
        [Parameter(Mandatory)][bool]$GiteeReleaseExists,
        [Parameter(Mandatory)][string]$Tag
    )

    if (-not $CachePresent -and ($GitHubReleaseExists -or $GiteeReleaseExists)) {
        throw "Remote Release $Tag already exists but its local package cache is missing; restore the original cache before retrying."
    }
}

function Save-GiteeAttachmentCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$State,
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()][AllowEmptyString()][string]$AttachmentId,
        [AllowNull()][AllowEmptyString()][string]$DownloadUrl,
        [Parameter(Mandatory)][ValidateSet('pending', 'uploaded', 'verified')][string]$Status
    )

    if ($null -eq $State.giteeAssets) { $State.giteeAssets = [ordered]@{} }
    $State.giteeAssets[$Name] = [ordered]@{
        status = $Status
        attachmentId = $AttachmentId
        downloadUrl = $DownloadUrl
    }
}

function New-DualSourceManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseManifestPath,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$GitHubRepository,
        [Parameter(Mandatory)][string]$GiteeReleasePageUrl,
        [Parameter(Mandatory)][string]$GiteeArchiveDownloadUrl,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $null = Get-ReleaseVersionFromTag $Tag
    $manifest = Get-Content -LiteralPath $BaseManifestPath -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    $githubArchiveUrl = "https://github.com/$GitHubRepository/releases/download/$Tag/$($manifest.archiveName)"
    if ($manifest.version -ne (Get-ReleaseVersionFromTag $Tag)) {
        throw "Base manifest does not describe $Tag."
    }

    $manifest.downloadUrl = $GiteeArchiveDownloadUrl
    $manifest.downloadMirrors = @($githubArchiveUrl)
    $manifest.releasePageUrl = $GiteeReleasePageUrl
    $json = ConvertTo-Json -InputObject $manifest -Depth 12
    $directory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [System.IO.File]::WriteAllText($OutputPath, $json, [System.Text.UTF8Encoding]::new($false))

    return $manifest
}

function Assert-ManifestUrlRepair {
    param(
        [string]$OldPath, [string]$NewPath, [string]$ExpectedOldSha256,
        [string]$Tag, [string]$GiteeOwner, [string]$GiteeRepository, [long]$ArchiveAttachmentId
    )
    if ((Get-ReleaseFileSha256 $OldPath) -cne $ExpectedOldSha256.ToLowerInvariant()) { throw 'Manifest repair old SHA-256 does not match the reviewed value.' }
    $old = Get-Content -LiteralPath $OldPath -Raw | ConvertFrom-Json -AsHashtable
    $new = Get-Content -LiteralPath $NewPath -Raw | ConvertFrom-Json -AsHashtable
    $archiveName = Get-ReleaseArchiveName $Tag
    if ($old.version -cne (Get-ReleaseVersionFromTag $Tag) -or $old.archiveName -cne $archiveName -or $ArchiveAttachmentId -le 0 -or
        $old.downloadUrl -cne "https://gitee.com/$GiteeOwner/$GiteeRepository/releases/download/$Tag/$archiveName" -or
        $new.downloadUrl -cne "https://gitee.com/$GiteeOwner/$GiteeRepository/attach_files/$ArchiveAttachmentId/download") {
        throw 'Manifest repair only permits the reviewed Gitee client URL alias migration for the same tag and attachment.'
    }
    foreach ($key in @(@($old.Keys) + @($new.Keys) | Select-Object -Unique)) {
        if ($key -ceq 'downloadUrl') { continue }
        if ((ConvertTo-Json -InputObject $old[$key] -Depth 12 -Compress) -cne (ConvertTo-Json -InputObject $new[$key] -Depth 12 -Compress)) {
            throw "Manifest repair may not change $key; archive identity, mirrors and release metadata are immutable."
        }
    }
}

Export-ModuleMember -Function Get-ReleaseVersionFromTag, Get-ReleaseArchiveName, Test-SuccessfulMainWindowsCiRun, New-GiteeReleasePayload, Get-ReleaseFileSha256, Assert-SafeGiteeDownloadUrl, Get-ReleaseBundle, New-ReleaseState, Save-ReleaseState, Read-ReleaseState, Assert-ReleaseCachePolicy, Save-GiteeAttachmentCheckpoint, New-DualSourceManifest, Assert-ManifestUrlRepair
