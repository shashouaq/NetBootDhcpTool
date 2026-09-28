[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [string]$GitHubRepository = 'shashouaq/NetBootDhcpTool',
    [string]$GiteeOwner = 'joel20230302',
    [string]$GiteeRepository = 'NetBootDhcpTool',
    [string]$Token = $env:GITEE_TOKEN,
    [switch]$LibraryOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:MirrorRoot = Split-Path -Parent $PSScriptRoot

function Invoke-MirrorHttp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [ValidateSet('Get', 'Post', 'Patch')][string]$Method = 'Get',
        [hashtable]$Headers = @{},
        [AllowEmptyString()][string]$Body,
        [string]$ContentType = 'application/json; charset=utf-8',
        [string]$OutFile,
        [string]$UploadFile,
        [hashtable]$FormFields = @{},
        [ValidateRange(1, 900)][int]$TimeoutSec = 45
    )

    if ($UploadFile) {
        if ($Method -cne 'Post') { throw 'Gitee attachment upload requires POST.' }
        if (-not $FormFields.ContainsKey('access_token')) { throw 'Gitee attachment upload requires its access_token form field.' }
        $curl = (Get-Command curl.exe -ErrorAction Stop).Source
        $responsePath = Join-Path ([System.IO.Path]::GetTempPath()) ('netboot-gitee-upload-' + [guid]::NewGuid().ToString('N') + '.json')
        $headersPath = $responsePath + '.headers'
        $escapedToken = ([string]$FormFields['access_token']).Replace('\', '\\').Replace('"', '\"')
        $curlConfig = 'form = "access_token={0}"' -f $escapedToken
        try {
            $curlOutput = $curlConfig | & $curl --config - --silent --show-error --http1.1 --connect-timeout 20 --max-time $TimeoutSec --fail-with-body --request POST --form "file=@$UploadFile;filename=$([System.IO.Path]::GetFileName($UploadFile))" --dump-header $headersPath --output $responsePath --write-out 'http=%{http_code} seconds=%{time_total}' $Uri 2>&1
            $exitCode = $LASTEXITCODE
            $stats = ($curlOutput | Out-String).Trim()
            $statusMatch = [regex]::Match($stats, '(?:^|\s)http=(?<status>\d{3})(?:\s|$)')
            $statusCode = if ($statusMatch.Success) { [int]$statusMatch.Groups['status'].Value } else { 0 }
            if ($statusCode -eq 0) { throw "Gitee attachment upload transport failed with curl exit code $exitCode." }
            $content = if (Test-Path -LiteralPath $responsePath -PathType Leaf) { [System.IO.File]::ReadAllText($responsePath) } else { '' }
            return [pscustomobject]@{ StatusCode = $statusCode; Content = $content; Headers = @{} }
        } finally {
            Remove-Item -LiteralPath $responsePath, $headersPath -Force -ErrorAction SilentlyContinue
        }
    }

    $parameters = @{
        Uri = $Uri
        Method = $Method
        Headers = $Headers
        SkipHttpErrorCheck = $true
        MaximumRedirection = 10
        TimeoutSec = $TimeoutSec
        ErrorAction = 'Stop'
    }
    if ($Method -in @('Post', 'Patch')) {
        $parameters.Body = $Body
        $parameters.ContentType = $ContentType
    }
    if ($OutFile) {
        $parameters.OutFile = $OutFile
        $parameters.PassThru = $true
    }
    try {
        $response = Invoke-WebRequest @parameters
        $content = if ($OutFile) { '' } else { [string]$response.Content }
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = $content; Headers = $response.Headers }
    } catch {
        throw "HTTPS request failed at $([uri]$Uri).Host ($Method). $($_.Exception.GetType().Name)"
    }
}

function Get-MirrorSha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-MirrorJson([string]$Content, [string]$Context) {
    if ([string]::IsNullOrWhiteSpace($Content)) { throw "$Context returned an empty response." }
    try {
        return ConvertFrom-Json -InputObject $Content -AsHashtable -NoEnumerate -ErrorAction Stop
    }
    catch { throw "$Context returned invalid JSON." }
}

function Get-GitHubReleaseForMirror([string]$Tag, [string]$Repository) {
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' }
    $uri = "https://api.github.com/repos/$Repository/releases/tags/$([uri]::EscapeDataString($Tag))"
    $response = Invoke-MirrorHttp -Uri $uri -Headers $headers
    if ($response.StatusCode -ne 200) { throw "GitHub Release lookup returned HTTP $($response.StatusCode) for $Tag." }
    $release = Get-MirrorJson $response.Content 'GitHub Release lookup'
    if ($release.tag_name -cne $Tag -or [bool]$release.draft -or [bool]$release.prerelease) {
        throw "GitHub Release $Tag is not a stable, published Release."
    }
    return $release
}

function Get-MirrorExpectedAssets([object]$Release, [string]$Tag, [string]$GitHubRepository, [string]$GiteeOwner, [string]$GiteeRepository) {
    $version = $Tag.Substring(1)
    $manifestAsset = @($Release.assets | Where-Object { $_.name -ceq 'latest.json' })
    $signatureAsset = @($Release.assets | Where-Object { $_.name -ceq 'latest.json.sig' })
    if ($manifestAsset.Count -ne 1 -or $signatureAsset.Count -ne 1) { throw "GitHub Release $Tag must contain one latest.json and one latest.json.sig." }

    $manifestPath = [System.IO.Path]::GetTempFileName()
    try {
        $manifestResponse = Invoke-MirrorHttp -Uri ([string]$manifestAsset[0].browser_download_url) -Headers @{ 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' } -OutFile $manifestPath
        if ($manifestResponse.StatusCode -ne 200) { throw 'Could not download latest.json from the published GitHub Release.' }
        $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
        $manifestText = [System.Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes)
        $manifest = Get-MirrorJson $manifestText 'GitHub latest.json'
    } finally { Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue }
    if ([string]$manifest.version -cne $version -or
        [string]$manifest.archiveName -cne "NetBootDhcpTool-v$version.7z" -or
        [string]$manifest.archiveSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'GitHub latest.json does not identify the requested tag and portable archive.'
    }
    $giteeBase = "https://gitee.com/$GiteeOwner/$GiteeRepository/releases/download/$Tag"
    $githubBase = "https://github.com/$GitHubRepository/releases/download/$Tag"
    $expectedArchiveUrl = "$giteeBase/$($manifest.archiveName)"
    $expectedArchiveMirror = "$githubBase/$($manifest.archiveName)"
    if ([string]$manifest.downloadUrl -cne $expectedArchiveUrl -or
        @($manifest.downloadMirrors | Where-Object { [string]$_ -ceq $expectedArchiveMirror }).Count -ne 1) {
        throw 'Signed archive URLs do not identify the matching current Gitee and GitHub Release assets.'
    }
    if (@($manifest.packages).Count -lt 1 -or @($manifest.packages).Count -gt 8 -or
        @($manifest.packages | Where-Object { $_.kind -ceq 'Full' }).Count -ne 1) {
        throw 'GitHub latest.json must declare exactly one Full package and no more than eight packages.'
    }

    $names = [System.Collections.Generic.List[string]]::new()
    $names.Add([string]$manifest.archiveName)
    $names.Add(([string]$manifest.archiveName + '.sha256'))
    foreach ($package in @($manifest.packages)) {
        if ([string]$package.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$package.size -le 0 -or
            [string]$package.fileName -notmatch '^NetBootDhcpTool-(full-v\d+\.\d+\.\d+|ota-v\d+\.\d+\.\d+-to-v\d+\.\d+\.\d+)\.zip$') {
            throw 'GitHub latest.json contains invalid update package metadata.'
        }
        if ([string]$package.kind -ceq 'Full') {
            if ([string]$package.fileName -cne "NetBootDhcpTool-full-v$version.zip") { throw 'Full package filename does not match the requested version.' }
        } elseif ([string]$package.kind -ceq 'Ota') {
            if ([string]$package.fileName -notmatch "^NetBootDhcpTool-ota-v\d+\.\d+\.\d+-to-v$([regex]::Escape($version))\.zip") {
                throw 'OTA package filename does not target the requested version.'
            }
            if ([string]$package.baseVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$package.baseVersion -ge [version]$version) {
                throw 'Release OTA metadata has an invalid base version.'
            }
        } else { throw 'Release package kind must be Full or Ota.' }
        $expectedPackageUrl = "$giteeBase/$($package.fileName)"
        $expectedPackageMirror = "$githubBase/$($package.fileName)"
        if ([string]$package.downloadUrl -cne $expectedPackageUrl -or
            @($package.downloadMirrors | Where-Object { [string]$_ -ceq $expectedPackageMirror }).Count -ne 1) {
            throw "Signed package URLs do not match the current mirrors for $($package.fileName)."
        }
        $names.Add([string]$package.fileName)
        $names.Add(([string]$package.fileName + '.sha256'))
    }
    $names.Add('latest.json.sig')
    $names.Add('latest.json')
    if ($names.Count -ne @($names | Select-Object -Unique).Count) { throw 'GitHub latest.json declares duplicate asset filenames.' }

    $releaseAssets = @($Release.assets)
    $duplicateNames = @($releaseAssets | Group-Object -Property name | Where-Object Count -gt 1)
    if ($duplicateNames.Count -gt 0) { throw 'GitHub Release contains duplicate asset filenames.' }
    foreach ($name in $names) {
        if (@($releaseAssets | Where-Object { $_.name -ceq $name }).Count -ne 1) {
            throw "GitHub Release is missing required asset $name."
        }
    }
    $unexpected = @($releaseAssets | Where-Object { $_.name -cnotin $names })
    if ($unexpected.Count -gt 0) { throw "GitHub Release contains an undeclared asset: $($unexpected[0].name)." }
    return [pscustomobject]@{ Manifest = $manifest; ManifestBytes = $manifestBytes; AssetNames = @($names); Assets = $releaseAssets }
}

function Get-MirrorTrustedPublicKeyPem {
    $sourcePath = Join-Path $script:MirrorRoot 'src\NetBootDhcpTool.Core\UpdatePackages.cs'
    $source = [System.IO.File]::ReadAllText($sourcePath)
    $match = [regex]::Match($source, '(?ms)public const string TrustedPublicKeyPem = """\r?\n(?<pem>.*?)\r?\n""";')
    if (-not $match.Success) { throw 'Could not read the update manifest public key from the client source.' }
    return $match.Groups['pem'].Value
}

function Assert-MirrorManifestSignature([byte[]]$ManifestBytes, [string]$SignatureText) {
    try { $signature = [Convert]::FromBase64String($SignatureText.Trim()) }
    catch { throw 'GitHub latest.json.sig is not valid Base64.' }
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem((Get-MirrorTrustedPublicKeyPem))
        if (-not $rsa.VerifyData($ManifestBytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)) {
            throw 'GitHub latest.json signature does not match the update key trusted by the client.'
        }
    } finally { $rsa.Dispose() }
}

function Get-GitHubTagCommitForMirror([string]$Tag, [string]$Repository) {
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' }
    $referenceResponse = Invoke-MirrorHttp -Uri "https://api.github.com/repos/$Repository/git/ref/tags/$([uri]::EscapeDataString($Tag))" -Headers $headers
    if ($referenceResponse.StatusCode -ne 200) { throw "GitHub tag ref lookup returned HTTP $($referenceResponse.StatusCode)." }
    $reference = Get-MirrorJson $referenceResponse.Content 'GitHub tag ref'
    $object = $reference.object
    for ($depth = 0; $depth -lt 4 -and $object.type -ceq 'tag'; $depth++) {
        $tagResponse = Invoke-MirrorHttp -Uri "https://api.github.com/repos/$Repository/git/tags/$($object.sha)" -Headers $headers
        if ($tagResponse.StatusCode -ne 200) { throw "GitHub annotated tag lookup returned HTTP $($tagResponse.StatusCode)." }
        $tagObject = Get-MirrorJson $tagResponse.Content 'GitHub annotated tag'
        $object = $tagObject.object
    }
    if ($object.type -cne 'commit' -or [string]$object.sha -notmatch '^[a-fA-F0-9]{40}$') { throw "Could not resolve $Tag to a full Git commit SHA." }
    return ([string]$object.sha).ToLowerInvariant()
}

function Invoke-MirrorGit {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$Operation)
    $gitArguments = @('-c', 'credential.helper=') + $Arguments
    $output = & git @gitArguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $detail = ($output -join ' ').Trim()
        if (-not [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) { $detail = $detail.Replace($env:GITEE_TOKEN, '[redacted]') }
        throw "Gitee Git $Operation failed with exit code $exitCode. $detail"
    }
    return ($output -join [System.Environment]::NewLine)
}

function Test-MirrorCommitAncestor([string]$Ancestor, [string]$Descendant) {
    $output = & git -c credential.helper= merge-base --is-ancestor $Ancestor $Descendant 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) { return $true }
    if ($exitCode -eq 1) { return $false }
    $detail = ($output -join ' ').Trim()
    throw "Git merge-base failed with exit code $exitCode. $detail"
}

function Sync-GiteeSourceRefs {
    param(
        [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
        [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$TagCommit,
        [Parameter(Mandatory)][string]$Owner,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Token
    )
    $repoRoot = [System.IO.Path]::GetFullPath($script:MirrorRoot)
    $worktree = [string](Invoke-MirrorGit -Arguments @('rev-parse', '--show-toplevel') -Operation 'locate the checked-out repository')
    if ([System.IO.Path]::GetFullPath($worktree.Trim()) -ine $repoRoot) {
        throw 'Run the Gitee mirror publisher from the checked-out NetBootDhcpTool repository.'
    }
    $shallow = [string](Invoke-MirrorGit -Arguments @('rev-parse', '--is-shallow-repository') -Operation 'inspect repository history')
    if ($shallow.Trim() -ceq 'true') {
        $null = Invoke-MirrorGit -Arguments @('fetch', '--unshallow', 'origin', 'main', '--tags') -Operation 'fetch complete GitHub release history'
    }
    $null = Invoke-MirrorGit -Arguments @('fetch', 'origin', '+refs/heads/main:refs/remotes/origin/main', '--tags') -Operation 'fetch GitHub main and tags'
    $githubMain = ([string](Invoke-MirrorGit -Arguments @('rev-parse', 'refs/remotes/origin/main') -Operation 'resolve GitHub main')).Trim()
    $fetchedTagCommit = ([string](Invoke-MirrorGit -Arguments @('rev-parse', ('refs/tags/' + $Tag + '^{commit}')) -Operation 'resolve the GitHub release tag')).Trim().ToLowerInvariant()
    if ($fetchedTagCommit -cne $TagCommit.ToLowerInvariant()) { throw "Fetched GitHub tag $Tag resolves to a different commit than the published Release." }
    if (-not (Test-MirrorCommitAncestor -Ancestor $fetchedTagCommit -Descendant $githubMain)) {
        throw "GitHub release tag $Tag is not contained in GitHub main."
    }

    $giteeUrl = "https://gitee.com/$Owner/$Repository.git"
    $credentialDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('netboot-gitee-git-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $credentialDirectory -Force | Out-Null
    $askPassScript = Join-Path $credentialDirectory 'askpass.ps1'
    $askPassCommand = Join-Path $credentialDirectory 'askpass.cmd'
    $askPassContent = @'
param([string]$Prompt)
if ($Prompt -match '(?i)username') { [Console]::WriteLine('joel20230302') }
else { [Console]::WriteLine($env:GITEE_TOKEN) }
'@
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($askPassScript, $askPassContent, $utf8)
    $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
    $askPassLine = '"' + $pwshPath + '" -NoLogo -NoProfile -File "' + $askPassScript + '" %*'
    $askPassText = '@echo off' + [System.Environment]::NewLine + $askPassLine + [System.Environment]::NewLine
    [System.IO.File]::WriteAllText($askPassCommand, $askPassText, [System.Text.Encoding]::ASCII)

    $priorAskPass = $env:GIT_ASKPASS
    $priorToken = $env:GITEE_TOKEN
    $priorPrompt = $env:GIT_TERMINAL_PROMPT
    $priorGcmInteractive = $env:GCM_INTERACTIVE
    $mainTargetRef = 'refs/netboot-mirror/main-target/' + $Tag
    $verifyTagRef = 'refs/netboot-mirror/verify/' + $Tag
    $tagPushedWithMain = $false
    try {
        $env:GIT_ASKPASS = $askPassCommand
        $env:GITEE_TOKEN = $Token
        $env:GIT_TERMINAL_PROMPT = '0'
        $env:GCM_INTERACTIVE = 'never'
        $null = Invoke-MirrorGit -Arguments @('fetch', $giteeUrl, '+refs/heads/main:refs/remotes/gitee/main') -Operation 'fetch Gitee main'
        $giteeMain = ([string](Invoke-MirrorGit -Arguments @('rev-parse', 'refs/remotes/gitee/main') -Operation 'resolve Gitee main')).Trim()
        if (-not (Test-MirrorCommitAncestor -Ancestor $giteeMain -Descendant $fetchedTagCommit)) {
            if (-not (Test-MirrorCommitAncestor -Ancestor $fetchedTagCommit -Descendant $giteeMain)) {
                throw 'Gitee main has diverged from the release tag; refusing to rewrite or overwrite Gitee history.'
            }
            Write-Host "[Gitee] Main is already ahead of $Tag; preserving the existing branch tip."
        } else {
            $null = Invoke-MirrorGit -Arguments @('update-ref', $mainTargetRef, $fetchedTagCommit) -Operation 'prepare the release commit ref'
            $mainRefSpec = $mainTargetRef + ':refs/heads/main'
            $tagRefSpec = 'refs/tags/' + $Tag + ':refs/tags/' + $Tag
            $null = Invoke-MirrorGit -Arguments @('push', $giteeUrl, $mainRefSpec, $tagRefSpec) -Operation "fast-forward main and publish tag $Tag"
            $tagPushedWithMain = $true
            Write-Host "[Gitee] Fast-forward synchronized main and tag $Tag."
        }
        if (-not $tagPushedWithMain) {
            $tagRefSpec = 'refs/tags/' + $Tag + ':refs/tags/' + $Tag
            $null = Invoke-MirrorGit -Arguments @('push', $giteeUrl, $tagRefSpec) -Operation "publish tag $Tag"
        }
        $tagFetchSpec = '+refs/tags/' + $Tag + ':' + $verifyTagRef
        $null = Invoke-MirrorGit -Arguments @('fetch', $giteeUrl, $tagFetchSpec) -Operation 'read back the Gitee release tag'
        $verifyCommitRef = $verifyTagRef + '^{commit}'
        $giteeTagCommit = ([string](Invoke-MirrorGit -Arguments @('rev-parse', $verifyCommitRef) -Operation 'resolve the Gitee release tag')).Trim().ToLowerInvariant()
        if ($giteeTagCommit -cne $fetchedTagCommit) { throw "Gitee tag $Tag does not resolve to the GitHub formal-release commit." }
        Write-Host "[Gitee] Source tag $Tag resolves to $giteeTagCommit."
    } finally {
        if ($null -eq $priorAskPass) { Remove-Item Env:GIT_ASKPASS -ErrorAction SilentlyContinue } else { $env:GIT_ASKPASS = $priorAskPass }
        if ($null -eq $priorToken) { Remove-Item Env:GITEE_TOKEN -ErrorAction SilentlyContinue } else { $env:GITEE_TOKEN = $priorToken }
        if ($null -eq $priorPrompt) { Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue } else { $env:GIT_TERMINAL_PROMPT = $priorPrompt }
        if ($null -eq $priorGcmInteractive) { Remove-Item Env:GCM_INTERACTIVE -ErrorAction SilentlyContinue } else { $env:GCM_INTERACTIVE = $priorGcmInteractive }
        & git -c credential.helper= update-ref -d $mainTargetRef 2>$null
        & git -c credential.helper= update-ref -d $verifyTagRef 2>$null
        $fullCredentialDirectory = [System.IO.Path]::GetFullPath($credentialDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        if ([System.IO.Path]::GetDirectoryName($fullCredentialDirectory) -ceq $tempRoot -and
            [System.IO.Path]::GetFileName($fullCredentialDirectory).StartsWith('netboot-gitee-git-', [System.StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $fullCredentialDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-GiteeApiHeaders([string]$Token) {
    @{ Authorization = "Bearer $Token"; Accept = 'application/json'; 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' }
}

function Get-GiteeReleaseForMirror([string]$Tag, [string]$Owner, [string]$Repository, [string]$Token) {
    $uri = "https://gitee.com/api/v5/repos/$Owner/$Repository/releases/tags/$([uri]::EscapeDataString($Tag))"
    $response = Invoke-MirrorHttp -Uri $uri -Headers (Get-GiteeApiHeaders $Token)
    if ($response.StatusCode -eq 404) { return $null }
    if ($response.StatusCode -ne 200) { throw "Gitee Release lookup returned HTTP $($response.StatusCode)." }
    $release = Get-MirrorJson $response.Content 'Gitee Release lookup'
    if ($null -eq $release) { return $null }
    if ([string]$release.tag_name -cne $Tag) { throw 'Gitee returned a Release with a different tag.' }
    return $release
}

function Get-GiteeAttachmentsForMirror([long]$ReleaseId, [string]$Owner, [string]$Repository, [string]$Token) {
    $items = [System.Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 10; $page++) {
        $uri = "https://gitee.com/api/v5/repos/$Owner/$Repository/releases/$ReleaseId/attach_files?page=$page&per_page=100"
        $response = Invoke-MirrorHttp -Uri $uri -Headers (Get-GiteeApiHeaders $Token)
        if ($response.StatusCode -ne 200) { throw "Gitee attachment listing returned HTTP $($response.StatusCode); refusing to upload without proving absence." }
        $pageItems = Get-MirrorJson $response.Content 'Gitee attachment listing'
        if ($pageItems -isnot [System.Collections.IList]) { throw 'Gitee attachment listing did not return an array.' }
        foreach ($item in $pageItems) { $items.Add($item) }
        if ($pageItems.Count -lt 100) { return @($items) }
    }
    throw 'Gitee attachment listing exceeded the pagination limit; attachment absence is unknown.'
}

function Get-GiteeMirrorDownloadUrl([object]$Attachment, [long]$ReleaseId, [string]$Owner, [string]$Repository, [string]$Tag, [string]$AssetName) {
    $url = [string]$Attachment.browser_download_url
    if ([string]::IsNullOrWhiteSpace($url)) {
        $url = "https://gitee.com/$Owner/$Repository/attach_files/$($Attachment.id)/download"
    }
    $parsed = $null
    if (-not [uri]::TryCreate($url, [UriKind]::Absolute, [ref]$parsed)) { throw 'Gitee attachment URL is invalid.' }
    if ($parsed.Scheme -cne 'https' -or $parsed.Host -cne 'gitee.com' -or $parsed.UserInfo -or $parsed.Query -or $parsed.Fragment) {
        throw 'Gitee attachment download must use a credential-free canonical HTTPS URL.'
    }
    $canonicalPath = "/$Owner/$Repository/releases/download/$Tag/$AssetName"
    $attachmentPattern = "^/$([regex]::Escape($Owner))/$([regex]::Escape($Repository))/attach_files/\d+(?:/download)?$"
    if ($parsed.AbsolutePath -cne $canonicalPath -and
        $parsed.AbsolutePath -notmatch $attachmentPattern) {
        throw "Gitee attachment URL is outside the expected repository for $AssetName."
    }
    return $url
}

function Read-VerifiedMirrorSidecar([string]$Path, [string]$Name, [string]$ExpectedHash) {
    $parts = ([System.IO.File]::ReadAllText($Path)).Trim() -split '\s+', 2
    if ($parts.Count -ne 2 -or $parts[0] -cne $ExpectedHash -or $parts[1] -cne $Name) {
        throw "SHA-256 sidecar content is invalid for $Name."
    }
}

function Download-VerifiedGitHubAsset([object]$Asset, [string]$Destination) {
    if ([long]$Asset.size -le 0 -or [string]$Asset.browser_download_url -notmatch '^https://github\.com/') {
        throw "GitHub asset metadata is invalid for $($Asset.name)."
    }
    $response = Invoke-MirrorHttp -Uri ([string]$Asset.browser_download_url) -Headers @{ 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' } -OutFile $Destination -TimeoutSec 180
    if ($response.StatusCode -ne 200 -or -not (Test-Path -LiteralPath $Destination -PathType Leaf)) { throw "GitHub download failed for $($Asset.name)." }
    if ([long](Get-Item -LiteralPath $Destination).Length -ne [long]$Asset.size) { throw "GitHub asset size mismatch for $($Asset.name)." }
}

function New-GiteeReleaseForMirror([string]$Tag, [string]$Owner, [string]$Repository, [string]$Token, [string]$TargetCommit, [object]$Manifest) {
    $existing = Get-GiteeReleaseForMirror $Tag $Owner $Repository $Token
    if ($null -ne $existing) {
        Write-Host "[Gitee] Reusing existing Release $Tag (ID $($existing.id))."
        return $existing
    }
    $payload = [ordered]@{
        tag_name = $Tag
        name = "NetBoot DHCP Tool $Tag"
        body = [string]$Manifest.releaseNotes
        target_commitish = $TargetCommit
        prerelease = $true
    }
    $uri = "https://gitee.com/api/v5/repos/$Owner/$Repository/releases"
    $response = Invoke-MirrorHttp -Uri $uri -Method Post -Headers (Get-GiteeApiHeaders $Token) -Body (ConvertTo-Json -InputObject $payload -Depth 8 -Compress)
    if ($response.StatusCode -notin @(200, 201)) {
        $recovered = Get-GiteeReleaseForMirror $Tag $Owner $Repository $Token
        if ($null -eq $recovered) { throw "Gitee Release creation returned HTTP $($response.StatusCode); no Release for $Tag was confirmed." }
        Write-Host "[Gitee] Recovered Release $Tag after an ambiguous create response."
        return $recovered
    }
    $created = Get-MirrorJson $response.Content 'Gitee Release creation'
    if ([string]$created.tag_name -cne $Tag -or [long]$created.id -le 0) { throw 'Gitee Release creation returned an invalid identity.' }
    Write-Host "[Gitee] Created prerelease $Tag (ID $($created.id))."
    return $created
}

function Publish-GiteeMirror {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
        [Parameter(Mandatory)][string]$Token,
        [string]$GitHubRepository = 'shashouaq/NetBootDhcpTool',
        [string]$GiteeOwner = 'joel20230302',
        [string]$GiteeRepository = 'NetBootDhcpTool'
    )

    if ([string]::IsNullOrWhiteSpace($Token) -or $Token -match '[\r\n]') { throw 'GITEE_TOKEN is required and must not contain a line break.' }
    foreach ($repoPart in @($GitHubRepository, "$GiteeOwner/$GiteeRepository")) {
        if ($repoPart -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository must be in owner/repository form.' }
    }
    $version = $Tag.Substring(1)
    $release = Get-GitHubReleaseForMirror $Tag $GitHubRepository
    $expected = Get-MirrorExpectedAssets $release $Tag $GitHubRepository $GiteeOwner $GiteeRepository
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('netboot-gitee-mirror-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    try {
        $localAssets = [ordered]@{}
        foreach ($name in $expected.AssetNames) {
            $asset = @($expected.Assets | Where-Object { $_.name -ceq $name })[0]
            $localPath = Join-Path $temporaryRoot $name
            Download-VerifiedGitHubAsset $asset $localPath
            $localAssets[$name] = [pscustomobject]@{ Path = $localPath; Sha256 = (Get-MirrorSha256 $localPath); Size = [long](Get-Item -LiteralPath $localPath).Length }
        }

        $manifestPath = $localAssets['latest.json'].Path
        $signatureText = [System.IO.File]::ReadAllText($localAssets['latest.json.sig'].Path)
        Assert-MirrorManifestSignature -ManifestBytes ([System.IO.File]::ReadAllBytes($manifestPath)) -SignatureText $signatureText
        $manifestText = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
        $manifest = Get-MirrorJson $manifestText 'GitHub latest.json'
        if ([string]$manifest.version -cne $version) { throw 'Signed manifest version does not match the requested tag.' }

        $archiveName = [string]$manifest.archiveName
        $archiveHash = $localAssets[$archiveName].Sha256
        if ($archiveHash -cne ([string]$manifest.archiveSha256).ToLowerInvariant()) { throw 'Portable archive SHA-256 does not match signed latest.json.' }
        Read-VerifiedMirrorSidecar $localAssets[($archiveName + '.sha256')].Path $archiveName $archiveHash
        foreach ($package in @($manifest.packages)) {
            $packageName = [string]$package.fileName
            $record = $localAssets[$packageName]
            if ($null -eq $record -or $record.Sha256 -cne ([string]$package.sha256).ToLowerInvariant() -or
                $record.Size -ne [long]$package.size) { throw "Update package does not match signed metadata: $packageName." }
            Read-VerifiedMirrorSidecar $localAssets[($packageName + '.sha256')].Path $packageName $record.Sha256
        }

        $targetCommit = Get-GitHubTagCommitForMirror $Tag $GitHubRepository
        Sync-GiteeSourceRefs -Tag $Tag -TagCommit $targetCommit -Owner $GiteeOwner -Repository $GiteeRepository -Token $Token
        $giteeRelease = New-GiteeReleaseForMirror $Tag $GiteeOwner $GiteeRepository $Token $targetCommit $manifest
        $releaseId = [long]$giteeRelease.id
        if ($releaseId -le 0) { throw "Gitee Release ID is invalid for $Tag." }
        $headers = Get-GiteeApiHeaders $Token

        $orderedNames = @($expected.AssetNames | Where-Object { $_ -notin @('latest.json', 'latest.json.sig') }) + @('latest.json.sig', 'latest.json')
        foreach ($name in $orderedNames) {
            $local = $localAssets[$name]
            $attachments = Get-GiteeAttachmentsForMirror $releaseId $GiteeOwner $GiteeRepository $Token
            $matches = @($attachments | Where-Object { [string]$_.name -ceq $name })
            if ($matches.Count -gt 1) { throw "Gitee Release has duplicate attachments named $name; refusing to delete or overwrite any of them." }
            if ($matches.Count -eq 0) {
                $uploadUri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$releaseId/attach_files"
                $upload = Invoke-MirrorHttp -Uri $uploadUri -Method Post -Headers $headers -UploadFile $local.Path -FormFields @{ access_token = $Token } -TimeoutSec 900
                if ($upload.StatusCode -notin @(200, 201)) {
                    $recovered = @(Get-GiteeAttachmentsForMirror $releaseId $GiteeOwner $GiteeRepository $Token | Where-Object { [string]$_.name -ceq $name })
                    if ($recovered.Count -ne 1) { throw "Gitee upload for $name returned HTTP $($upload.StatusCode); no unique attachment was recovered. Rerun after the API is available." }
                    $matches = $recovered
                    Write-Host "[Gitee] Recovered $name after an ambiguous upload response."
                } else {
                    $created = Get-MirrorJson $upload.Content "Gitee upload for $name"
                    if ([string]$created.name -cne $name -or [long]$created.id -le 0) { throw "Gitee upload returned an invalid attachment identity for $name." }
                    $matches = @(Get-GiteeAttachmentsForMirror $releaseId $GiteeOwner $GiteeRepository $Token | Where-Object { [string]$_.name -ceq $name })
                    if ($matches.Count -ne 1 -or [string]$matches[0].id -cne [string]$created.id) {
                        throw "Gitee did not expose exactly one attachment named $name after upload; rerun will reconcile it."
                    }
                    Write-Host "[Gitee] Uploaded $name."
                }
            } else {
                Write-Host "[Gitee] Reusing existing $name after full download and SHA-256 verification."
            }

            $readbackPath = Join-Path $temporaryRoot ('gitee-' + [guid]::NewGuid().ToString('N') + '-' + $name)
            $downloadUrl = Get-GiteeMirrorDownloadUrl $matches[0] $releaseId $GiteeOwner $GiteeRepository $Tag $name
            $readback = Invoke-MirrorHttp -Uri $downloadUrl -Headers @{ 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' } -OutFile $readbackPath -TimeoutSec 180
            if ($readback.StatusCode -ne 200 -or -not (Test-Path -LiteralPath $readbackPath -PathType Leaf) -or
                [long](Get-Item -LiteralPath $readbackPath).Length -ne $local.Size -or
                (Get-MirrorSha256 $readbackPath) -cne $local.Sha256) {
                throw "Gitee attachment readback size/SHA-256 mismatch for $name; existing attachment is preserved."
            }
        }

        $allAttachments = Get-GiteeAttachmentsForMirror $releaseId $GiteeOwner $GiteeRepository $Token
        foreach ($attachment in $allAttachments) {
            if ([string]$attachment.name -cnotin $expected.AssetNames) { throw "Gitee Release contains an undeclared attachment: $($attachment.name)." }
        }
        foreach ($name in $expected.AssetNames) {
            $count = @($allAttachments | Where-Object { [string]$_.name -ceq $name }).Count
            if ($count -ne 1) { throw "Gitee final inventory has $count attachments named $name; expected exactly one." }
        }

        if ([bool]$giteeRelease.prerelease) {
            $publishUri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$releaseId"
            $payload = ConvertTo-Json -InputObject @{
                tag_name = $Tag
                name = [string]$giteeRelease.name
                body = [string]$giteeRelease.body
                prerelease = $false
            } -Depth 8 -Compress
            $published = Invoke-MirrorHttp -Uri $publishUri -Method Patch -Headers $headers -Body $payload
            if ($published.StatusCode -notin @(200, 201)) { throw "Gitee Release promotion returned HTTP $($published.StatusCode)." }
            $giteeRelease = Get-MirrorJson $published.Content 'Gitee Release promotion'
            if ([bool]$giteeRelease.prerelease) { throw 'Gitee Release remained a prerelease after promotion.' }
        }

        $latestUri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/latest"
        $latestResponse = Invoke-MirrorHttp -Uri $latestUri -Headers $headers
        if ($latestResponse.StatusCode -ne 200) { throw "Gitee latest Release lookup returned HTTP $($latestResponse.StatusCode)." }
        $latestRelease = Get-MirrorJson $latestResponse.Content 'Gitee latest Release'
        if ([string]$latestRelease.tag_name -cne $Tag) { throw "Gitee latest Release is $($latestRelease.tag_name), expected $Tag." }

        foreach ($name in @($archiveName) + @($manifest.packages | ForEach-Object { [string]$_.fileName })) {
            $attachment = @($allAttachments | Where-Object { [string]$_.name -ceq $name })[0]
            $path = Join-Path $temporaryRoot ('final-' + [guid]::NewGuid().ToString('N') + '-' + $name)
            $uri = Get-GiteeMirrorDownloadUrl $attachment $releaseId $GiteeOwner $GiteeRepository $Tag $name
            $response = Invoke-MirrorHttp -Uri $uri -Headers @{ 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' } -OutFile $path -TimeoutSec 180
            if ($response.StatusCode -ne 200 -or (Get-MirrorSha256 $path) -cne $localAssets[$name].Sha256) {
                throw "Final public Gitee download SHA-256 mismatch for $name."
            }
            Write-Host "[Gitee] Final public download verified $name ($($localAssets[$name].Sha256))."
        }
        foreach ($name in @('latest.json.sig', 'latest.json')) {
            $attachment = @($allAttachments | Where-Object { [string]$_.name -ceq $name })[0]
            $path = Join-Path $temporaryRoot ('final-' + [guid]::NewGuid().ToString('N') + '-' + $name)
            $uri = Get-GiteeMirrorDownloadUrl $attachment $releaseId $GiteeOwner $GiteeRepository $Tag $name
            $response = Invoke-MirrorHttp -Uri $uri -Headers @{ 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' } -OutFile $path -TimeoutSec 60
            if ($response.StatusCode -ne 200 -or (Get-MirrorSha256 $path) -cne $localAssets[$name].Sha256) {
                throw "Final public Gitee download SHA-256 mismatch for $name."
            }
        }
        $success = "GITEE_MIRROR SUCCESS tag=$Tag assets=$($expected.AssetNames.Count) archive_sha256=$archiveHash"
        Write-Host $success
        if ($env:GITHUB_STEP_SUMMARY) {
            $fullPackageName = [string](@($manifest.packages | Where-Object { $_.kind -ceq 'Full' })[0].fileName)
            $fullPackageHash = $localAssets[$fullPackageName].Sha256
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value @"
## Gitee Mirror SUCCESS

- Tag: $Tag
- Reused or published assets: $($expected.AssetNames.Count)
- Portable archive SHA-256: $archiveHash
- Full package SHA-256: $fullPackageHash
- Gitee stable Release: https://gitee.com/$GiteeOwner/$GiteeRepository/releases/tag/$Tag
"@ -Encoding utf8
        }
        return [pscustomobject]@{ Succeeded = $true; Tag = $Tag; AssetCount = $expected.AssetNames.Count; ArchiveSha256 = $archiveHash; ReleaseId = $releaseId }
    } finally {
        $tempFull = [System.IO.Path]::GetFullPath($temporaryRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        if ([System.IO.Path]::GetDirectoryName($tempFull) -ceq $tempBase -and [System.IO.Path]::GetFileName($tempFull).StartsWith('netboot-gitee-mirror-', [System.StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $tempFull -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if (-not $LibraryOnly) {
    $null = Publish-GiteeMirror -Tag $Tag -Token $Token -GitHubRepository $GitHubRepository -GiteeOwner $GiteeOwner -GiteeRepository $GiteeRepository
}
