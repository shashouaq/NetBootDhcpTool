$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'scripts\Publish-GiteeMirror.ps1') -Tag 'v1.0.20' -LibraryOnly
$priorGitHubToken = $env:GITHUB_TOKEN
$priorGhToken = $env:GH_TOKEN
$env:GITHUB_TOKEN = 'test-github-token'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('netboot-gitee-mirror-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$script:MirrorRoot = $testRoot
$testRsa = [System.Security.Cryptography.RSA]::Create(3072)
$script:TestPublicKey = $testRsa.ExportSubjectPublicKeyInfoPem()
function Get-MirrorTrustedPublicKeyPem { return $script:TestPublicKey }

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-MirrorFixture {
    param([string]$Root, [string]$Tag = 'v1.0.20')
    $files = [ordered]@{}
    $archiveName = 'NetBootDhcpTool-v1.0.20.7z'
    $fullName = 'NetBootDhcpTool-full-v1.0.20.7z'
    $setupName = 'NetBootDhcpTool-Setup-v1.0.20.exe'
    [System.IO.File]::WriteAllBytes((Join-Path $Root $archiveName), [byte[]]::new(128))
    [System.IO.File]::WriteAllBytes((Join-Path $Root $fullName), [byte[]]::new(256))
    [System.IO.File]::WriteAllBytes((Join-Path $Root $setupName), [byte[]]::new(512))
    foreach ($name in @($archiveName, $fullName)) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $Root $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText((Join-Path $Root ($name + '.sha256')), "$hash  $name", [System.Text.Encoding]::ASCII)
    }
    $archiveHash = (Get-FileHash -LiteralPath (Join-Path $Root $archiveName) -Algorithm SHA256).Hash.ToLowerInvariant()
    $fullHash = (Get-FileHash -LiteralPath (Join-Path $Root $fullName) -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        version = '1.0.20'
        archiveName = $archiveName
        archiveSha256 = $archiveHash
        downloadUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/$Tag/$archiveName"
        downloadMirrors = @("https://github.com/shashouaq/NetBootDhcpTool/releases/download/$Tag/$archiveName")
        releasePageUrl = 'https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/v1.0.20'
        releaseNotes = 'Chinese / English release notes'
        packages = @()
        sevenZipPackages = @([ordered]@{
            kind = 'Full'
            fileName = $fullName
            sha256 = $fullHash
            size = [long](Get-Item -LiteralPath (Join-Path $Root $fullName)).Length
            downloadUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/$Tag/$fullName"
            downloadMirrors = @("https://github.com/shashouaq/NetBootDhcpTool/releases/download/$Tag/$fullName")
            baseVersion = $null
            baseInstallManifestSha256 = $null
        })
    }
    $manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $manifest -Depth 10))
    [System.IO.File]::WriteAllBytes((Join-Path $Root 'latest-v2.json'), $manifestBytes)
    $signature = $testRsa.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
    [System.IO.File]::WriteAllText((Join-Path $Root 'latest-v2.json.sig'), [Convert]::ToBase64String($signature) + [System.Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

    $legacyRoot = Join-Path $repoRoot 'build\legacy\v1.0.20'
    foreach ($legacyName in @('latest.json', 'latest.json.sig')) {
        Copy-Item -LiteralPath (Join-Path $legacyRoot $legacyName) -Destination (Join-Path $Root $legacyName)
    }

    $assetNames = @($archiveName, ($archiveName + '.sha256'), $fullName, ($fullName + '.sha256'), $setupName, 'latest-v2.json.sig', 'latest-v2.json', 'latest.json.sig', 'latest.json')
    $githubAssets = @()
    $sourceMap = @{}
    foreach ($name in $assetNames) {
        $path = Join-Path $Root $name
        $sourceMap["https://github.com/shashouaq/NetBootDhcpTool/releases/download/$Tag/$name"] = $path
        $githubAssets += [ordered]@{
            name = $name
            size = [long](Get-Item -LiteralPath $path).Length
            digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            browser_download_url = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/$Tag/$name"
        }
    }
    return [pscustomobject]@{
        Tag = $Tag
        Version = '1.0.20'
        Assets = $assetNames
        SourceMap = $sourceMap
        Release = [ordered]@{ id = 123; tag_name = $Tag; draft = $false; prerelease = ($Tag -like '*-rc.*'); assets = $githubAssets }
        ArchiveName = $archiveName
        ArchiveHash = $archiveHash
    }
}

function Reset-MirrorMock([object]$Fixture) {
    $global:GiteeMirrorMock = @{
        Fixture = $Fixture
        Release = $null
        Attachments = [System.Collections.Generic.List[object]]::new()
        UploadNames = [System.Collections.Generic.List[string]]::new()
        GiteeFiles = @{}
        NextAttachmentId = 500
        UploadCount = 0
        LoseUploadResponse = $false
        LoseCreateResponse = $false
        GiteeApiCallCount = 0
        EventLog = [System.Collections.Generic.List[string]]::new()
    }
}

function Invoke-MirrorHttp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [ValidateSet('Get', 'Post', 'Patch')][string]$Method = 'Get',
        [hashtable]$Headers = @{},
        [AllowEmptyString()][string]$Body,
        [string]$ContentType = 'application/json; charset=utf-8',
        [string]$OutFile,
        [long]$ExpectedSize=-1,
        [string]$ExpectedSha256,
        [string]$UploadFile,
        [hashtable]$FormFields = @{},
        [ValidateRange(1, 900)][int]$TimeoutSec = 45
    )
    $uriObject = [uri]$Uri
    $content = ''
    $status = 200
    if ($UploadFile) {
        Assert-True ($FormFields.access_token -ceq 'test-token') 'Gitee multipart upload must include its access_token form field.'
        Assert-True ($TimeoutSec -ge 30 -and $TimeoutSec -le 300) 'Gitee uploads must use a bounded timeout without long retries.'
        $global:GiteeMirrorMock.UploadCount++
        $name = [System.IO.Path]::GetFileName($UploadFile)
        $global:GiteeMirrorMock.UploadNames.Add($name)
        $global:GiteeMirrorMock.EventLog.Add("upload:$name")
        $id = $global:GiteeMirrorMock.NextAttachmentId++
        $destination = Join-Path $testRoot ('gitee-' + $id + '-' + $name)
        Copy-Item -LiteralPath $UploadFile -Destination $destination | Out-Null
        $global:GiteeMirrorMock.GiteeFiles[[string]$id] = $destination
        $attachment = [ordered]@{ id = $id; name = $name; size = [long](Get-Item -LiteralPath $destination).Length; browser_download_url = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/$($global:GiteeMirrorMock.Fixture.Tag)/$name" }
        $global:GiteeMirrorMock.Attachments.Add($attachment)
        $content = ConvertTo-Json -InputObject $attachment -Compress
        if($global:GiteeMirrorMock.LoseUploadResponse){$global:GiteeMirrorMock.LoseUploadResponse=$false;throw '[eof] unexpected EOF after storage'}
    } elseif ($uriObject.Host -eq 'api.github.com' -and $uriObject.AbsolutePath -match '/releases/tags/v1\.0\.20(?:-rc\.1)?$') {
        Assert-True ($Headers.Authorization -ceq 'Bearer test-github-token') 'GitHub Release REST requests must use the supplied read-only token.'
        $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Fixture.Release -Depth 8 -Compress
    } elseif ($uriObject.Host -eq 'api.github.com' -and $uriObject.AbsolutePath -match '/git/ref/tags/v1\.0\.20(?:-rc\.1)?$') {
        Assert-True ($Headers.Authorization -ceq 'Bearer test-github-token') 'GitHub tag REST requests must use the supplied read-only token.'
        $content = '{"object":{"type":"commit","sha":"0123456789012345678901234567890123456789"}}'
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/tags/v1\.0\.20(?:-rc\.1)?$') {
        $global:GiteeMirrorMock.GiteeApiCallCount++
        if ($null -eq $global:GiteeMirrorMock.Release) { $content = 'null' }
        else { $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Release -Depth 8 -Compress }
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/latest$') {
        $global:GiteeMirrorMock.GiteeApiCallCount++
        if ($null -eq $global:GiteeMirrorMock.Release -or $global:GiteeMirrorMock.Release.prerelease) { $status = 404 }
        else { $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Release -Depth 8 -Compress }
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases$' -and $Method -eq 'Post') {
        $global:GiteeMirrorMock.GiteeApiCallCount++
        $payload = ConvertFrom-Json -InputObject $Body -AsHashtable
        $global:GiteeMirrorMock.Release = [ordered]@{ id = 456; tag_name = [string]$payload.tag_name; name = [string]$payload.name; body = [string]$payload.body; prerelease = [bool]$payload.prerelease }
        $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Release -Depth 8 -Compress
        if($Method -eq 'Post' -and $global:GiteeMirrorMock.LoseCreateResponse){$global:GiteeMirrorMock.LoseCreateResponse=$false;throw '[eof] unexpected EOF after creation'}
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/456/attach_files/(?<id>\d+)$' -and $Method -eq 'Get') {
        $global:GiteeMirrorMock.GiteeApiCallCount++
        $id = $Matches.id
        $global:GiteeMirrorMock.EventLog.Add("lookup:$id")
        $attachment = @($global:GiteeMirrorMock.Attachments | Where-Object { [string]$_.id -ceq $id } | Select-Object -First 1)
        if ($attachment.Count -eq 0) { $status = 404 }
        else { $content = ConvertTo-Json -InputObject $attachment[0] -Depth 8 -Compress }
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/456/attach_files$' -and $Method -eq 'Get') {
        $global:GiteeMirrorMock.GiteeApiCallCount++
        $items = @($global:GiteeMirrorMock.Attachments.ToArray())
        $itemJson = @($items | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 8 -Compress })
        $content = '[' + ($itemJson -join ',') + ']'
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/456$' -and $Method -eq 'Patch') {
        $global:GiteeMirrorMock.GiteeApiCallCount++
        $global:GiteeMirrorMock.EventLog.Add('promote')
        $global:GiteeMirrorMock.Release.prerelease = $false
        $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Release -Depth 8 -Compress
    } elseif ($uriObject.Host -eq 'github.com' -and $global:GiteeMirrorMock.Fixture.SourceMap.ContainsKey($Uri)) {
        Assert-True (-not $Headers.ContainsKey('Authorization')) 'Public Release downloads must not receive the GitHub API token.'
        $source = $global:GiteeMirrorMock.Fixture.SourceMap[$Uri]
        if ($OutFile) { Copy-Item -LiteralPath $source -Destination $OutFile | Out-Null }
        else { $content = [System.IO.File]::ReadAllText($source) }
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/download/v1\.0\.20(?:-rc\.1)?/(?<name>[^/]+)$') {
        $global:GiteeMirrorMock.EventLog.Add("public:$($Matches.name)")
        $name = $Matches.name
        $attachment = @($global:GiteeMirrorMock.Attachments | Where-Object { $_.name -ceq $name } | Select-Object -First 1)
        if ($attachment.Count -eq 0) { $status = 404 }
        else {
            $source = $global:GiteeMirrorMock.GiteeFiles[[string]$attachment[0].id]
            if ($OutFile) { Copy-Item -LiteralPath $source -Destination $OutFile | Out-Null }
            else { $content = [System.IO.File]::ReadAllText($source) }
        }
    } else {
        $status = 404
    }
    return [pscustomobject]@{ StatusCode = $status; Content = $content; Headers = @{}; TimeSeconds = 0.01 }
}

try {
    $fixture = New-MirrorFixture $testRoot
    Reset-MirrorMock $fixture
    foreach ($generated in @('v1.0.20.zip', 'v1.0.20.tar.gz')) {
        $id = if ($generated.EndsWith('.zip', [System.StringComparison]::Ordinal)) { 880 } else { 881 }
        $global:GiteeMirrorMock.Attachments.Add([ordered]@{ id = $id; name = $generated; browser_download_url = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.20/$generated" })
    }
    $first = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token'
    Assert-True $first.Succeeded 'Initial mirror run did not succeed.'
    Assert-True ($global:GiteeMirrorMock.UploadCount -eq $fixture.Assets.Count) 'Initial run did not upload each formal asset exactly once.'
    Assert-True ($global:GiteeMirrorMock.GiteeApiCallCount -gt 0) 'The publisher did not use the Gitee REST API.'
    Assert-True (($global:GiteeMirrorMock.UploadNames[0] -ceq 'NetBootDhcpTool-v1.0.20.7z.sha256') -and
        ($global:GiteeMirrorMock.UploadNames[1] -ceq 'NetBootDhcpTool-full-v1.0.20.7z.sha256')) 'Small package sidecars should upload before large archives.'
    Assert-True (($global:GiteeMirrorMock.UploadNames[-2] -ceq 'latest-v2.json.sig') -and
        ($global:GiteeMirrorMock.UploadNames[-1] -ceq 'latest-v2.json')) 'Signed V2 manifest assets must remain the final uploads.'
    Assert-True (-not $global:GiteeMirrorMock.Release.prerelease) 'Gitee Release was not promoted after validation.'
    Assert-True ($first.ArchiveSha256 -ceq $fixture.ArchiveHash) 'Final archive SHA-256 differs from the GitHub source.'
    Assert-True ($first.UploadTimings.Count -eq $fixture.Assets.Count) 'Per-asset transfer times were not returned.'
    foreach ($assetName in $fixture.Assets) {
        $timing = $first.UploadTimings[$assetName]
        Assert-True ($timing.Filename -ceq $assetName -and $timing.Size -gt 0 -and $timing.HttpStatus -eq 200 -and $timing.AttachmentId -gt 0 -and
            -not [string]::IsNullOrWhiteSpace($timing.StartedAtUtc) -and -not [string]::IsNullOrWhiteSpace($timing.EndedAtUtc)) "Transfer telemetry is incomplete for $assetName."
    }
    foreach ($assetName in $global:GiteeMirrorMock.UploadNames) {
        $uploadIndex = $global:GiteeMirrorMock.EventLog.IndexOf("upload:$assetName")
        $idEventIndex = $uploadIndex + 1
        while ($idEventIndex -lt $global:GiteeMirrorMock.EventLog.Count -and $global:GiteeMirrorMock.EventLog[$idEventIndex] -notlike 'lookup:*') { $idEventIndex++ }
        Assert-True ($idEventIndex -lt $global:GiteeMirrorMock.EventLog.Count) "Uploaded $assetName was not queried by attachment ID."
        $publicIndex = $idEventIndex + 1
        while ($publicIndex -lt $global:GiteeMirrorMock.EventLog.Count -and $global:GiteeMirrorMock.EventLog[$publicIndex] -notlike 'public:*') { $publicIndex++ }
        Assert-True ($publicIndex -lt $global:GiteeMirrorMock.EventLog.Count -and $global:GiteeMirrorMock.EventLog[$publicIndex] -ceq "public:$assetName") "Uploaded $assetName was not publicly downloaded and verified before the next asset."
    }
    Assert-True (-not ($first.UploadTimings.Keys -contains 'v1.0.20.zip') -and -not ($first.UploadTimings.Keys -contains 'v1.0.20.tar.gz')) 'Gitee-generated source archives must not enter the formal asset candidate set.'
    $promotionIndex = $global:GiteeMirrorMock.EventLog.IndexOf('promote')
    $publicEventsBeforePromotion = @($global:GiteeMirrorMock.EventLog | Select-Object -First $promotionIndex | Where-Object { $_ -like 'public:*' })
    Assert-True ($publicEventsBeforePromotion.Count -ge ($fixture.Assets.Count * 2)) 'Every newly uploaded asset must be immediately downloaded and then final-verified before Release promotion.'
    $uploadCount = $global:GiteeMirrorMock.UploadCount

    $assetDirectory = Join-Path $testRoot 'artifacts\gitee-mirror-assets\v1.0.20'
    $second = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token' -AssetDirectory $assetDirectory
    Assert-True $second.Succeeded 'Idempotent rerun did not succeed.'
    Assert-True ($global:GiteeMirrorMock.UploadCount -eq $uploadCount) 'Rerun uploaded duplicate assets instead of reusing verified attachments.'
    $formalAttachments = @($global:GiteeMirrorMock.Attachments | Where-Object { $fixture.Assets -contains $_.name })
    Assert-True ($formalAttachments.Count -eq $fixture.Assets.Count) 'Rerun changed the unique formal attachment count.'

    $badAssetDirectory = Join-Path $testRoot 'tampered-assets'
    New-Item -ItemType Directory -Path $badAssetDirectory -Force | Out-Null
    foreach ($name in $fixture.Assets) { Copy-Item -LiteralPath (Join-Path $testRoot $name) -Destination (Join-Path $badAssetDirectory $name) }
    [System.IO.File]::WriteAllBytes((Join-Path $badAssetDirectory $fixture.ArchiveName), [byte[]](1, 2, 3, 4))
    Reset-MirrorMock $fixture
    $localHashMismatch = $false
    try { $null = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token' -AssetDirectory $badAssetDirectory }
    catch { $localHashMismatch = $_.Exception.Message -match 'GitHub asset (size|SHA-256) mismatch' }
    Assert-True $localHashMismatch 'AssetDirectory must hard fail when a local formal asset differs from GitHub REST size/SHA-256.'
    Assert-True ($global:GiteeMirrorMock.GiteeApiCallCount -eq 0) 'A local-asset mismatch must fail before any Gitee API write or lookup.'
    Assert-True ($global:GiteeMirrorMock.UploadCount -eq 0) 'A local-asset mismatch must not upload any attachment.'

    $extraAssetDirectory = Join-Path $testRoot 'extra-assets'
    New-Item -ItemType Directory -Path $extraAssetDirectory -Force | Out-Null
    foreach ($name in $fixture.Assets) { Copy-Item -LiteralPath (Join-Path $testRoot $name) -Destination (Join-Path $extraAssetDirectory $name) }
    [System.IO.File]::WriteAllText((Join-Path $extraAssetDirectory 'v1.0.20.zip'), 'not a formal asset')
    Reset-MirrorMock $fixture
    $extraAssetRejected = $false
    try { $null = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token' -AssetDirectory $extraAssetDirectory }
    catch { $extraAssetRejected = $_.Exception.Message -match 'must contain exactly the formal GitHub Release assets' }
    Assert-True $extraAssetRejected 'Unexpected local files such as generated source archives must hard fail.'
    Assert-True ($global:GiteeMirrorMock.GiteeApiCallCount -eq 0 -and $global:GiteeMirrorMock.UploadCount -eq 0) 'Unexpected local files must fail before Gitee access.'

    Reset-MirrorMock $fixture
    $global:GiteeMirrorMock.Release = [ordered]@{ id = 456; tag_name = $fixture.Tag; name = 'NetBoot DHCP Tool'; body = ''; prerelease = $false }
    $badPath = Join-Path $testRoot 'wrong-content.bin'
    [System.IO.File]::WriteAllBytes($badPath, [byte[]](99, 98, 97))
    $global:GiteeMirrorMock.GiteeFiles['499'] = $badPath
    $global:GiteeMirrorMock.Attachments.Add([ordered]@{ id = 499; name = $fixture.ArchiveName; browser_download_url = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.20/$($fixture.ArchiveName)" })
    $mismatchFailed = $false
    try { $null = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token' }
    catch { $mismatchFailed = $_.Exception.Message -match 'size/SHA-256 mismatch' }
    Assert-True $mismatchFailed 'An existing same-name asset with different bytes must hard fail.'
    Assert-True ($global:GiteeMirrorMock.UploadCount -eq 0) 'Mismatched existing bytes must not trigger an upload.'
    Assert-True (Test-Path -LiteralPath $badPath) 'Mismatch handling must preserve the existing attachment bytes.'

    Reset-MirrorMock $fixture
    $global:GiteeMirrorMock.Release = [ordered]@{ id = 456; tag_name = $fixture.Tag; name = 'NetBoot DHCP Tool'; body = ''; prerelease = $false }
    $global:GiteeMirrorMock.Attachments.Add([ordered]@{ id = 1; name = $fixture.ArchiveName; browser_download_url = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.20/$($fixture.ArchiveName)" })
    $global:GiteeMirrorMock.Attachments.Add([ordered]@{ id = 2; name = $fixture.ArchiveName; browser_download_url = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.20/$($fixture.ArchiveName)" })
    $duplicateFailed = $false
    try { $null = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token' }
    catch { $duplicateFailed = $_.Exception.Message -match 'duplicate attachments named' }
    Assert-True $duplicateFailed 'Duplicate same-name attachments must hard fail without deletion.'
    Assert-True ($global:GiteeMirrorMock.Attachments.Count -eq 2) 'Duplicate handling must preserve both existing attachments.'

    $script:GiteeReleaseAssetMaxBytes = $null
      foreach ($size in @(50000000L, 67419433L)) {
          $sizeWarnings = @()
          Assert-GiteeAssetSizePreflight -AssetName 'within-limit.7z' -Size $size -WarningVariable sizeWarnings -WarningAction SilentlyContinue
          Assert-True ($sizeWarnings.Count -eq 0) "A $size-byte asset should be permitted without a size warning."
      }
      $separateMigrationAssets = [ordered]@{
          'NetBootDhcpTool-Setup-v1.1.0.exe' = [pscustomobject]@{ Size = 46262743L }
          'NetBootDhcpTool-full-v1.1.0.7z' = [pscustomobject]@{ Size = 58256825L }
      }
      $separateAssetWarnings = @()
      Assert-GiteeAssetSetSizePreflight -Assets $separateMigrationAssets -WarningVariable separateAssetWarnings -WarningAction SilentlyContinue
      Assert-True ((($separateMigrationAssets.Values | Measure-Object -Property Size -Sum).Sum) -gt 95000000L -and $separateAssetWarnings.Count -eq 0) 'Two separately downloadable assets may exceed 95 MB combined when each remains below the per-asset warning threshold.'
      $nearLimitWarnings = @()
    Assert-GiteeAssetSizePreflight -AssetName 'near-limit.zip' -Size 99999999L -WarningVariable nearLimitWarnings -WarningAction SilentlyContinue
    Assert-True ($nearLimitWarnings.Count -eq 1) 'An asset above 95 MB must warn while the hard limit is unknown.'
    $script:GiteeReleaseAssetMaxBytes = 100000000L
    $syntheticLimitRejected = $false
    try { Assert-GiteeAssetSizePreflight -AssetName 'at-limit.zip' -Size 100000000L }
    catch { $syntheticLimitRejected = $_.Exception.Message -match 'before upload' }
    Assert-True $syntheticLimitRejected 'The configured synthetic release-asset limit must reject files at or above the limit.'

    Reset-MirrorMock $fixture
    $script:GiteeReleaseAssetMaxBytes = 1L
    $noWriteBeforeReject = $false
    try { $null = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token' -AssetDirectory $assetDirectory }
    catch { $noWriteBeforeReject = $_.Exception.Message -match 'before upload' }
    Assert-True $noWriteBeforeReject 'An over-limit formal asset must fail during local preflight.'
    Assert-True ($global:GiteeMirrorMock.GiteeApiCallCount -eq 0 -and $global:GiteeMirrorMock.UploadCount -eq 0) 'Size rejection must happen before any Gitee API call or HTTP POST.'
    $script:GiteeReleaseAssetMaxBytes = $null

    $candidateRoot = Join-Path $testRoot 'candidate-source'
    New-Item -ItemType Directory -Path $candidateRoot | Out-Null
    $candidateFixture = New-MirrorFixture $candidateRoot 'v1.0.20-rc.1'
    Reset-MirrorMock $candidateFixture
    $script:MirrorIsCandidate = $true
    $candidate = Publish-GiteeMirror -Tag $candidateFixture.Tag -Token 'test-token' -AssetDirectory $candidateRoot
    Assert-True ($candidate.AssetCount -eq $candidateFixture.Assets.Count -or $candidate.UploadTimings.Count -eq $candidateFixture.Assets.Count) 'RC assets must pass the same complete public readback.'
    Assert-True ($global:GiteeMirrorMock.Release.prerelease -and -not $global:GiteeMirrorMock.EventLog.Contains('promote')) 'An RC must never promote Gitee stable.'
    $candidateUploadCount = $global:GiteeMirrorMock.UploadCount
    foreach ($generated in @("$($candidateFixture.Tag).zip", "$($candidateFixture.Tag).tar.gz")) {
        $global:GiteeMirrorMock.Attachments.Add([ordered]@{ id = 999; name = $generated; browser_download_url = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/$($candidateFixture.Tag)/$generated" })
    }
    $null = Publish-GiteeMirror -Tag $candidateFixture.Tag -Token 'test-token' -AssetDirectory $candidateRoot
    Assert-True ($global:GiteeMirrorMock.UploadCount -eq $candidateUploadCount) 'Re-running an RC mirror must reuse exact assets and ignore source archives.'
    $global:GiteeMirrorMock.Release.prerelease = $false
    $stableReuseRejected = $false
    try { $null = Publish-GiteeMirror -Tag $candidateFixture.Tag -Token 'test-token' -AssetDirectory $candidateRoot }
    catch { $stableReuseRejected = $_.Exception.Message -match 'stable Gitee Release' }
    Assert-True $stableReuseRejected 'A stable Gitee Release cannot be repurposed as an RC.'
    $script:MirrorIsCandidate = $false

    $script:MirrorIsCandidate=$false
    $lostFixtureRoot=Join-Path $testRoot 'lost-response'
    New-Item -ItemType Directory $lostFixtureRoot|Out-Null
    $lostFixture=New-MirrorFixture $lostFixtureRoot
    Reset-MirrorMock $lostFixture
    $global:GiteeMirrorMock.LoseUploadResponse=$true
    $global:GiteeMirrorMock.LoseCreateResponse=$true
    $recovered=Publish-GiteeMirror -Tag $lostFixture.Tag -Token 'test-token' -AssetDirectory $lostFixtureRoot
    Assert-True ($global:GiteeMirrorMock.UploadCount -eq $lostFixture.Assets.Count) 'Lost upload reply caused duplicate POST.'
    Assert-True (@($recovered.UploadTimings.Values|Where-Object State -EQ RECOVERED).Count -eq 1) 'Lost stored upload was not recovered by size/SHA readback.'
    Write-Output 'GITEE_UNKNOWN_WRITE_RECONCILIATION_OK create=1 upload=1 duplicate_posts=0'
    Write-Output 'GITEE_MIRROR_TESTS_OK api_only=1 asset_directory=1 local_hash_hard_fail=1 promote_after_public_hash=1 resume=1 mismatched_sha_hard_fail=1 duplicates_preserved=1 size_preflight=1 reject_before_gitee_write=1'
} finally {
    if ($null -eq $priorGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $priorGitHubToken }
    if ($null -eq $priorGhToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $priorGhToken }
    $testRsa.Dispose()
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Variable GiteeMirrorMock -Scope Global -ErrorAction SilentlyContinue
}
