$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'scripts\Publish-GiteeMirror.ps1') -Tag 'v1.0.20' -LibraryOnly
$priorGitHubToken = $env:GITHUB_TOKEN
$priorGhToken = $env:GH_TOKEN
$env:GITHUB_TOKEN = 'test-github-token'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('netboot-gitee-mirror-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$testRsa = [System.Security.Cryptography.RSA]::Create(3072)
$script:TestPublicKey = $testRsa.ExportSubjectPublicKeyInfoPem()
function Get-MirrorTrustedPublicKeyPem { return $script:TestPublicKey }

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-MirrorFixture {
    param([string]$Root)
    $files = [ordered]@{}
    $archiveName = 'NetBootDhcpTool-v1.0.20.7z'
    $fullName = 'NetBootDhcpTool-full-v1.0.20.zip'
    [System.IO.File]::WriteAllBytes((Join-Path $Root $archiveName), [byte[]]::new(128))
    [System.IO.File]::WriteAllBytes((Join-Path $Root $fullName), [byte[]]::new(256))
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
        downloadUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.20/$archiveName"
        downloadMirrors = @("https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.20/$archiveName")
        releasePageUrl = 'https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/v1.0.20'
        releaseNotes = 'Chinese / English release notes'
        packages = @([ordered]@{
            kind = 'Full'
            fileName = $fullName
            sha256 = $fullHash
            size = [long](Get-Item -LiteralPath (Join-Path $Root $fullName)).Length
            downloadUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.20/$fullName"
            downloadMirrors = @("https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.20/$fullName")
            baseVersion = $null
            baseInstallManifestSha256 = $null
        })
    }
    $manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $manifest -Depth 10))
    [System.IO.File]::WriteAllBytes((Join-Path $Root 'latest.json'), $manifestBytes)
    $signature = $testRsa.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
    [System.IO.File]::WriteAllText((Join-Path $Root 'latest.json.sig'), [Convert]::ToBase64String($signature) + [System.Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

    $assetNames = @($archiveName, ($archiveName + '.sha256'), $fullName, ($fullName + '.sha256'), 'latest.json.sig', 'latest.json')
    $githubAssets = @()
    $sourceMap = @{}
    foreach ($name in $assetNames) {
        $path = Join-Path $Root $name
        $sourceMap["https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.20/$name"] = $path
        $githubAssets += [ordered]@{
            name = $name
            size = [long](Get-Item -LiteralPath $path).Length
            browser_download_url = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.20/$name"
        }
    }
    return [pscustomobject]@{
        Tag = 'v1.0.20'
        Version = '1.0.20'
        Assets = $assetNames
        SourceMap = $sourceMap
        Release = [ordered]@{ id = 123; tag_name = 'v1.0.20'; draft = $false; prerelease = $false; assets = $githubAssets }
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
        SourceSyncCount = 0
    }
}

function Sync-GiteeSourceRefs {
    param([string]$Tag, [string]$TagCommit, [string]$Owner, [string]$Repository, [string]$Token)
    Assert-True ($Tag -ceq 'v1.0.20' -and $TagCommit -ceq '0123456789012345678901234567890123456789' -and $Token -ceq 'test-token') 'Gitee source synchronization must use the exact published tag and protected token.'
    $global:GiteeMirrorMock.SourceSyncCount++
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
        [string]$UploadFile,
        [hashtable]$FormFields = @{},
        [ValidateRange(1, 900)][int]$TimeoutSec = 45
    )
    $uriObject = [uri]$Uri
    $content = ''
    $status = 200
    if ($UploadFile) {
        Assert-True ($FormFields.access_token -ceq 'test-token') 'Gitee multipart upload must include its access_token form field.'
        Assert-True ($TimeoutSec -ge 900) 'Gitee package uploads must allow enough time for large release assets.'
        $global:GiteeMirrorMock.UploadCount++
        $name = [System.IO.Path]::GetFileName($UploadFile)
        $global:GiteeMirrorMock.UploadNames.Add($name)
        $id = $global:GiteeMirrorMock.NextAttachmentId++
        $destination = Join-Path $testRoot ('gitee-' + $id + '-' + $name)
        Copy-Item -LiteralPath $UploadFile -Destination $destination | Out-Null
        $global:GiteeMirrorMock.GiteeFiles[[string]$id] = $destination
        $attachment = [ordered]@{ id = $id; name = $name; browser_download_url = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.20/$name" }
        $global:GiteeMirrorMock.Attachments.Add($attachment)
        $content = ConvertTo-Json -InputObject $attachment -Compress
    } elseif ($uriObject.Host -eq 'api.github.com' -and $uriObject.AbsolutePath -match '/releases/tags/v1\.0\.20$') {
        Assert-True ($Headers.Authorization -ceq 'Bearer test-github-token') 'GitHub Release REST requests must use the supplied read-only token.'
        $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Fixture.Release -Depth 8 -Compress
    } elseif ($uriObject.Host -eq 'api.github.com' -and $uriObject.AbsolutePath -match '/git/ref/tags/v1\.0\.20$') {
        Assert-True ($Headers.Authorization -ceq 'Bearer test-github-token') 'GitHub tag REST requests must use the supplied read-only token.'
        $content = '{"object":{"type":"commit","sha":"0123456789012345678901234567890123456789"}}'
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/tags/v1\.0\.20$') {
        if ($null -eq $global:GiteeMirrorMock.Release) { $content = 'null' }
        else { $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Release -Depth 8 -Compress }
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/latest$') {
        if ($null -eq $global:GiteeMirrorMock.Release -or $global:GiteeMirrorMock.Release.prerelease) { $status = 404 }
        else { $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Release -Depth 8 -Compress }
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases$' -and $Method -eq 'Post') {
        $payload = ConvertFrom-Json -InputObject $Body -AsHashtable
        $global:GiteeMirrorMock.Release = [ordered]@{ id = 456; tag_name = [string]$payload.tag_name; name = [string]$payload.name; body = [string]$payload.body; prerelease = [bool]$payload.prerelease }
        $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Release -Depth 8 -Compress
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/456/attach_files$' -and $Method -eq 'Get') {
        $items = @($global:GiteeMirrorMock.Attachments.ToArray())
        $content = if ($items.Count -eq 0) { '[]' } else { ConvertTo-Json -InputObject $items -Depth 8 -Compress }
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/456$' -and $Method -eq 'Patch') {
        $global:GiteeMirrorMock.Release.prerelease = $false
        $content = ConvertTo-Json -InputObject $global:GiteeMirrorMock.Release -Depth 8 -Compress
    } elseif ($uriObject.Host -eq 'github.com' -and $global:GiteeMirrorMock.Fixture.SourceMap.ContainsKey($Uri)) {
        Assert-True (-not $Headers.ContainsKey('Authorization')) 'Public Release downloads must not receive the GitHub API token.'
        $source = $global:GiteeMirrorMock.Fixture.SourceMap[$Uri]
        if ($OutFile) { Copy-Item -LiteralPath $source -Destination $OutFile | Out-Null }
        else { $content = [System.IO.File]::ReadAllText($source) }
    } elseif ($uriObject.Host -eq 'gitee.com' -and $uriObject.AbsolutePath -match '/releases/download/v1\.0\.20/(?<name>[^/]+)$') {
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
    return [pscustomobject]@{ StatusCode = $status; Content = $content; Headers = @{} }
}

try {
    $fixture = New-MirrorFixture $testRoot
    Reset-MirrorMock $fixture
    $first = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token'
    Assert-True $first.Succeeded 'Initial mirror run did not succeed.'
    Assert-True ($global:GiteeMirrorMock.SourceSyncCount -eq 1) 'The source tag must be synchronized before Gitee Release creation.'
    Assert-True ($global:GiteeMirrorMock.UploadCount -eq $fixture.Assets.Count) 'Initial run did not upload each formal asset exactly once.'
    Assert-True (($global:GiteeMirrorMock.UploadNames[0] -ceq 'NetBootDhcpTool-v1.0.20.7z.sha256') -and
        ($global:GiteeMirrorMock.UploadNames[1] -ceq 'NetBootDhcpTool-full-v1.0.20.zip.sha256')) 'Small package sidecars should upload before large archives.'
    Assert-True (($global:GiteeMirrorMock.UploadNames[-2] -ceq 'latest.json.sig') -and
        ($global:GiteeMirrorMock.UploadNames[-1] -ceq 'latest.json')) 'Signed manifest assets must remain the final uploads.'
    Assert-True (-not $global:GiteeMirrorMock.Release.prerelease) 'Gitee Release was not promoted after validation.'
    Assert-True ($first.ArchiveSha256 -ceq $fixture.ArchiveHash) 'Final archive SHA-256 differs from the GitHub source.'
    $uploadCount = $global:GiteeMirrorMock.UploadCount

    $second = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token'
    Assert-True $second.Succeeded 'Idempotent rerun did not succeed.'
    Assert-True ($global:GiteeMirrorMock.SourceSyncCount -eq 2) 'Same-tag resume must recheck the source tag without duplicating package uploads.'
    Assert-True ($global:GiteeMirrorMock.UploadCount -eq $uploadCount) 'Rerun uploaded duplicate assets instead of reusing verified attachments.'
    Assert-True ($global:GiteeMirrorMock.Attachments.Count -eq $fixture.Assets.Count) 'Rerun changed the unique attachment count.'

    Reset-MirrorMock $fixture
    $global:GiteeMirrorMock.Release = [ordered]@{ id = 456; tag_name = $fixture.Tag; name = 'NetBoot DHCP Tool'; body = ''; prerelease = $false }
    $badPath = Join-Path $testRoot 'wrong-content.bin'
    [System.IO.File]::WriteAllBytes($badPath, [byte[]](99, 98, 97))
    $global:GiteeMirrorMock.GiteeFiles['499'] = $badPath
    $global:GiteeMirrorMock.Attachments.Add([ordered]@{ id = 499; name = $fixture.ArchiveName; browser_download_url = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.20/$($fixture.ArchiveName)" })
    $mismatchFailed = $false
    try { $null = Publish-GiteeMirror -Tag $fixture.Tag -Token 'test-token' }
    catch { $mismatchFailed = $_.Exception.Message -match 'size/SHA-256 (preflight )?mismatch' }
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

    Write-Output 'GITEE_MIRROR_TESTS_OK create=1 resume=1 mismatched_sha_hard_fail=1 duplicates_preserved=1'
} finally {
    if ($null -eq $priorGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $priorGitHubToken }
    if ($null -eq $priorGhToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $priorGhToken }
    $testRsa.Dispose()
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Variable GiteeMirrorMock -Scope Global -ErrorAction SilentlyContinue
}
