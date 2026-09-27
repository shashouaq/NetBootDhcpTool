$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $repoRoot 'build/release-pipeline/ReleaseState.psm1') -Force
Import-Module (Join-Path $repoRoot 'build/release-pipeline/ReleaseTransport.psm1') -Force
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if (($Expected -join '|') -cne ($Actual -join '|')) { throw "$Message (expected $Expected, got $Actual)" }
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "Unexpected failure: $($_.Exception.Message); expected $Pattern" }
        return
    }
    throw "Expected failure matching $Pattern"
}

# Inject faults into the actual transport functions, without any live API calls.
$transport = Get-Module ReleaseTransport
& $transport {
    $script:attempts = 0
    $script:delays = @()
    function script:Start-Sleep { param($Seconds); $script:delays += $Seconds }
    function script:Invoke-WebRequest {
        $script:attempts++
        $status = @(503, 429, 200)[[Math]::Min(2, $script:attempts - 1)]
        return @{ StatusCode = $status; Headers = @{'Retry-After' = $(if ($status -eq 429) { '7' } else { '' })}; Content = '{}' }
    }
    $response = Invoke-ReleaseHttp -Uri 'https://gitee.com/api/test'
    if ($response.StatusCode -ne 200 -or $script:attempts -ne 3 -or ($script:delays -join ',') -ne '2,7') { throw '503/429 recovery did not honor bounded backoff and Retry-After.' }
    $script:attempts = 0
    $null = Invoke-ReleaseHttp -Uri 'https://gitee.com/api/test' -Method Post -Body '{}'
    if ($script:attempts -ne 1) { throw 'Ambiguous POST must not be blindly retried.' }
    function script:Invoke-WebRequest { $script:attempts++; return @{StatusCode=401;Headers=@{};Content='unauthorized'} }
    $script:attempts = 0
    $null = Invoke-ReleaseHttp -Uri 'https://gitee.com/api/test'
    if ($script:attempts -ne 1) { throw 'Permanent authentication failure must not be retried.' }
    function script:Invoke-WebRequest { $script:attempts++; throw 'connection reset' }
    $script:attempts = 0
    try { $null = Invoke-ReleaseHttp -Uri 'https://gitee.com/api/test'; throw 'unexpected success' }
    catch { if ($_.Exception.Message -notmatch 'transport failed after 4') { throw } }
    if ($script:attempts -ne 4) { throw 'Transport retries must have a finite bound.' }
    function script:Invoke-WebRequest { return @{StatusCode=429;Headers=@{'Retry-After'='600'};Content='limited'} }
    try { $null = Invoke-ReleaseHttp -Uri 'https://gitee.com/api/test'; throw 'unexpected success' }
    catch { if ($_.Exception.Message -notmatch 'retry budget') { throw } }
    # curl can create an empty header file when DNS/TLS fails before any response.
    # PowerShell Get-Content -Raw then returns null; retry parsing must accept it.
    $script:attempts=0
    function script:Get-Command { return @{Source='Invoke-FakeCurl'} }
    function script:Invoke-FakeCurl {
        $script:attempts++
        $headerIndex=[array]::IndexOf($args,'--dump-header')
        [IO.File]::WriteAllText($args[$headerIndex+1], '')
        if ($script:attempts -eq 1) { $global:LASTEXITCODE=28; return 'http=000' }
        $outputIndex=[array]::IndexOf($args,'--output')
        [IO.File]::WriteAllText($args[$outputIndex+1], 'complete bytes')
        $global:LASTEXITCODE=0
        return 'http=200'
    }
    $download=Join-Path $env:TEMP ('netboot-empty-header-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-ReleaseDownload -Uri 'https://gitee.com/download/file' -Destination $download -AssetName file
        if ($script:attempts -ne 2 -or (Get-Content -LiteralPath $download -Raw) -cne 'complete bytes') { throw 'Empty-header timeout did not recover.' }
    } finally { Remove-Item -LiteralPath $download, "$download.headers" -Force -ErrorAction SilentlyContinue }
}
Import-Module (Join-Path $repoRoot 'build/release-pipeline/ReleaseTransport.psm1') -Force

$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'build/publish-release.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors -join "`n") }
# Load production function definitions without running the publisher entry point.
$definitions = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)
$publisherFunctions = [scriptblock]::Create(($definitions.Extent.Text -join "`n"))
$tempRoot = Join-Path $env:TEMP ('netboot-resilience-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$Tag = 'v1.2.3'
$expectedArchiveName = "NetBootDhcpTool-$Tag.7z"
$GitHubRepository = 'owner/repo'; $GiteeOwner = 'owner'; $GiteeRepository = 'repo'
$stateDirectory = $tempRoot
$statePath = Join-Path $tempRoot 'release-state.json'
$archive = Join-Path $tempRoot $expectedArchiveName
[IO.File]::WriteAllText($archive, 'original bytes')
$hash = Get-ReleaseFileSha256 $archive
$bundle = @{ ArchivePath=$archive; ChecksumPath="$archive.sha256"; ArchiveName=$expectedArchiveName; ArchiveSha256=$hash; AssetFiles=@($archive, "$archive.sha256") }
[IO.File]::WriteAllText($bundle.ChecksumPath, "$hash  $expectedArchiveName")
$finalManifestPath = Join-Path $tempRoot 'latest.json'
[IO.File]::WriteAllText($finalManifestPath, '{"version":"1.2.3"}')
$finalSignaturePath = Join-Path $tempRoot 'latest.json.sig'
[IO.File]::WriteAllText($finalSignaturePath, 'test-signature')
$script:verifiedAssets = @{}
$script:releaseWarnings = [System.Collections.Generic.List[string]]::new()
$savedToken = $env:GITEE_TOKEN
$env:GITEE_TOKEN = 'test-only-token'
try {
    # A manifest repair is a narrowly reviewed exception; changing any other
    # release field or guessing the old digest must fail before remote mutation.
    $oldManifestPath = Join-Path $tempRoot 'old-manifest.json'
    $newManifestPath = Join-Path $tempRoot 'new-manifest.json'
    $oldManifest = @{version='1.2.3';archiveName=$expectedArchiveName;archiveSha256=$hash;downloadUrl="https://gitee.com/owner/repo/releases/download/$Tag/$expectedArchiveName";downloadMirrors=@("https://github.com/owner/repo/releases/download/$Tag/$expectedArchiveName")}
    [IO.File]::WriteAllText($oldManifestPath, ($oldManifest | ConvertTo-Json -Depth 8))
    $oldManifestHash = Get-ReleaseFileSha256 $oldManifestPath
    $newManifest = $oldManifest.Clone()
    $newManifest.downloadUrl='https://gitee.com/owner/repo/attach_files/456/download'
    [IO.File]::WriteAllText($newManifestPath, ($newManifest | ConvertTo-Json -Depth 8))
    $repairArguments=@{OldPath=$oldManifestPath;NewPath=$newManifestPath;ExpectedOldSha256=$oldManifestHash;Tag=$Tag;GiteeOwner='owner';GiteeRepository='repo';ArchiveAttachmentId=456}
    Assert-ManifestUrlRepair @repairArguments
    $repairArguments.ExpectedOldSha256='f'*64
    Assert-Fails { Assert-ManifestUrlRepair @repairArguments } 'old SHA-256'
    $repairArguments.ExpectedOldSha256=$oldManifestHash
    $newManifest.archiveSha256='f'*64
    [IO.File]::WriteAllText($newManifestPath, ($newManifest | ConvertTo-Json -Depth 8))
    Assert-Fails { Assert-ManifestUrlRepair @repairArguments } 'may not change archiveSha256'
    $newManifest.archiveSha256=$hash
    $newManifest.version='1.2.4'
    [IO.File]::WriteAllText($newManifestPath, ($newManifest | ConvertTo-Json -Depth 8))
    Assert-Fails { Assert-ManifestUrlRepair @repairArguments } 'may not change version'
    $newManifest.version='1.2.3'
    [IO.File]::WriteAllText($newManifestPath, ($newManifest | ConvertTo-Json -Depth 8))
    & {
        . $publisherFunctions
        $script:releaseState=New-ReleaseState -Tag $Tag -SourceCommit ('a'*40) -ArchiveSha256 $hash
        $script:releaseState.giteeReleaseId=123
        Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $expectedArchiveName -AttachmentId '456' -DownloadUrl 'https://gitee.com/download/archive' -Status verified
        $RepairManifestSha256=$oldManifestHash
        $finalManifestPath=$newManifestPath
        $script:ghPresent=$true; $script:giteePresent=$true; $script:ghFixed=$false; $script:giteeFixed=$false
        $script:deletes=0; $script:replacements=0
        function Get-GitHubAsset { if($script:ghPresent){return @{apiUrl='repos/owner/repo/releases/assets/111'}}; return $null }
        function Get-GiteeAttachments { if($script:giteePresent){return @(@{id=222;name='latest.json';browser_download_url='https://gitee.com/download/manifest'})}; return @() }
        function Download-GitHubAsset($Name,$DestinationDirectory) {
            $path=Join-Path $DestinationDirectory $Name
            Copy-Item -LiteralPath $(if($script:ghFixed){$newManifestPath}else{$oldManifestPath}) -Destination $path -Force
            return $path
        }
        function Invoke-ReleaseDownload { param($Uri,$Destination,$AssetName); Copy-Item -LiteralPath $(if($script:giteeFixed){$newManifestPath}else{$oldManifestPath}) -Destination $Destination -Force }
        function Invoke-Gh { $script:deletes++; $script:ghPresent=$false; return @{ExitCode=0;Output=''} }
        function Invoke-ReleaseHttp { $script:deletes++; $script:giteePresent=$false; return @{StatusCode=204} }
        function Ensure-GitHubAsset { $script:ghPresent=$true; $script:ghFixed=$true; $script:replacements++ }
        function Ensure-GiteeAsset { $script:giteePresent=$true; $script:giteeFixed=$true; $script:replacements++ }
        Repair-ClientManifest
        Assert-Equal 2 $script:deletes 'Repair deletes only the two reviewed manifest assets'
        Assert-Equal 2 $script:replacements 'Repair replaces each reviewed manifest once'
        Repair-ClientManifest
        Assert-Equal 2 $script:deletes 'Repeating a completed manifest repair must not delete again'
        Assert-Equal 2 $script:replacements 'Repeating a completed manifest repair must not upload again'
        $script:releaseState.manifestRepair.mirrors.Gitee.status='delete-pending'
        $script:giteePresent=$false
        Repair-ClientManifest
        Assert-Equal 2 $script:deletes 'An interrupted deletion must resume without another delete'
        Assert-Equal 3 $script:replacements 'Only the unfinished mirror may resume publication'
    }
    & {
        . $publisherFunctions
        $script:uploads = 0
        function Start-Sleep { }
        function Get-GitHubAsset { return $null }
        function Invoke-Gh {
            $script:uploads++
            if ($script:uploads -eq 1) { return @{ExitCode=1;Output='wsarecv: connection forcibly closed by remote host'} }
            return @{ExitCode=0;Output='uploaded'}
        }
        function Download-GitHubAsset($Name, $DestinationDirectory) {
            New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
            $destination = Join-Path $DestinationDirectory $Name
            Copy-Item -LiteralPath $archive -Destination $destination
            return $destination
        }
        Ensure-GitHubAsset -LocalPath $archive -Name $expectedArchiveName -ArchiveSha256 $hash
        Assert-Equal 2 $script:uploads 'A confirmed-absent GitHub upload must recover from the v1.0.17 reset'
        Assert-Equal $hash $script:verifiedAssets["GitHub/$expectedArchiveName"] 'Recovered upload must pass full byte verification'
        $script:uploads = 0
        $script:lookups = 0
        function Get-GitHubAsset { $script:lookups++; if ($script:lookups -gt 1) { return @{size=(Get-Item $archive).Length} }; return $null }
        Ensure-GitHubAsset -LocalPath $archive -Name $expectedArchiveName -ArchiveSha256 $hash
        Assert-Equal 1 $script:uploads 'Lost response with a stored asset must not cause a second upload'
        function Get-GitHubAsset { return @{size=(Get-Item $archive).Length} }
        function Download-GitHubAsset($Name, $DestinationDirectory) {
            New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
            $destination = Join-Path $DestinationDirectory $Name
            [IO.File]::WriteAllText($destination, 'tampered bytes')
            return $destination
        }
        $script:uploads = 0
        Assert-Fails { Ensure-GitHubAsset -LocalPath $archive -Name $expectedArchiveName -ArchiveSha256 $hash } 'SHA-256 mismatch'
        Assert-Equal 0 $script:uploads 'Hash mismatch must never overwrite or reupload'
        function Get-GitHubAsset { return $null }
        function Invoke-Gh { $script:uploads++; return @{ExitCode=1;Output='HTTP 503 service unavailable'} }
        $script:uploads=0
        Assert-Fails { Ensure-GitHubAsset -LocalPath $archive -Name $expectedArchiveName -ArchiveSha256 $hash } 'after 4 attempt'
        Assert-Equal 4 $script:uploads 'An absent GitHub upload must hard fail when the retry budget is exhausted'
    }
    & {
        . $publisherFunctions
        function Get-GitHubRelease { return @{assets=@(@{name=$expectedArchiveName},@{name=$expectedArchiveName})} }
        Assert-Fails { Get-GitHubAsset $expectedArchiveName } 'duplicate'
        $script:releaseState = New-ReleaseState -Tag $Tag -SourceCommit ('a'*40) -ArchiveSha256 $hash
        $script:releaseState.giteeReleaseId = 123
        Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $expectedArchiveName -AttachmentId '456' -DownloadUrl 'https://gitee.com/download/archive' -Status uploaded
        $script:listCalls = 0; $script:uploadCalls = 0
        function Get-GiteeAttachments { $script:listCalls++; throw 'list timeout' }
        function Upload-GiteeAsset { $script:uploadCalls++; throw 'unexpected upload' }
        function Invoke-CurlDownload { param($Uri,$Destination,$AssetName); Copy-Item -LiteralPath $archive -Destination $Destination }
        Ensure-GiteeAsset -LocalPath $archive -Name $expectedArchiveName -ArchiveSha256 $hash
        Assert-Equal 0 $script:listCalls 'Saved attachment ID and URL must bypass list API'
        Assert-Equal 0 $script:uploadCalls 'Saved attachment must not be reuploaded'
        Assert-Equal verified $script:releaseState.giteeAssets[$expectedArchiveName].status 'A saved upload must reach verified after direct readback'
        function Invoke-CurlDownload { param($Uri,$Destination,$AssetName); [IO.File]::WriteAllText($Destination,'tampered bytes') }
        Assert-Fails { Ensure-GiteeAsset -LocalPath $archive -Name $expectedArchiveName -ArchiveSha256 $hash } 'SHA-256 mismatch'
        Assert-Equal '456' $script:releaseState.giteeAssets[$expectedArchiveName].attachmentId 'Hash failure must retain known identity'
        Assert-Equal 0 $script:uploadCalls 'Hash failure must not upload a duplicate'
        Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $expectedArchiveName -AttachmentId '' -DownloadUrl '' -Status pending
        Assert-Fails { Ensure-GiteeAsset -LocalPath $archive -Name $expectedArchiveName -ArchiveSha256 $hash } 'list timeout'
        Assert-Equal 0 $script:uploadCalls 'Uncertain upload plus unavailable recovery inventory must hard fail'
    }
    & {
        . $publisherFunctions
        $script:uploadCalls = 0
        function Get-Command { return @{Source='Invoke-FakeCurl'} }
        function Invoke-FakeCurl { $script:uploadCalls++; $global:LASTEXITCODE=56; return 'http=000 connection reset' }
        function Find-GiteeAssetForRecovery { return $null }
        Assert-Fails { Upload-GiteeAsset -LocalPath $archive -Name $expectedArchiveName } 'no matching attachment'
        Assert-Equal 1 $script:uploadCalls 'Gitee ambiguous POST must not be blindly repeated'
        function Invoke-FakeCurl {
            $script:uploadCalls++
            $index = [array]::IndexOf($args, '--output')
            [IO.File]::WriteAllText($args[$index+1], (@{id=456;name=$expectedArchiveName;browser_download_url='https://gitee.com/download/archive'} | ConvertTo-Json))
            $global:LASTEXITCODE=0
            return 'http=201'
        }
        function Test-GiteeAttachment { throw 'download unavailable' }
        Assert-Fails { Upload-GiteeAsset -LocalPath $archive -Name $expectedArchiveName } 'download unavailable'
        Assert-Equal '456' $script:releaseState.giteeAssets[$expectedArchiveName].attachmentId 'Upload response identity must survive failed verification'
        Assert-Equal uploaded $script:releaseState.giteeAssets[$expectedArchiveName].status 'Unverified upload must remain resumable'
    }
    & {
        . $publisherFunctions
        foreach ($name in @($expectedArchiveName,"$expectedArchiveName.sha256",'latest.json','latest.json.sig')) {
            Save-GiteeAttachmentCheckpoint -State $script:releaseState -Name $name -AttachmentId '456' -DownloadUrl "https://gitee.com/download/$name" -Status verified
        }
        function Invoke-ReleaseDownload {
            param($Uri,$Destination,$AssetName)
            $source = if ($AssetName -like 'latest.json.sig*') { $finalSignaturePath } elseif ($AssetName.StartsWith('latest.json')) { $finalManifestPath } elseif ($AssetName.EndsWith('.sha256')) { $bundle.ChecksumPath } else { $archive }
            Copy-Item -LiteralPath $source -Destination $Destination
        }
        function Invoke-ReleaseHttp { return @{StatusCode=200;Content='{"id":123,"tag_name":"v1.2.3","prerelease":false}'} }
        function Get-GiteeAttachments { throw 'list timeout' }
        $warningsBefore = $script:releaseWarnings.Count
        Assert-PublicRelease
        Assert-Equal ($warningsBefore+1) $script:releaseWarnings.Count 'Only verified public bundles permit inventory warning'
        function Invoke-ReleaseDownload { throw 'download unavailable' }
        Assert-Fails { Assert-PublicRelease } 'download unavailable'
        Assert-Equal ($warningsBefore+1) $script:releaseWarnings.Count 'Unverified public bundle must not be downgraded to warning'
        function Invoke-ReleaseDownload {
            param($Uri,$Destination,$AssetName)
            $source = if ($AssetName -like 'latest.json.sig*') { $finalSignaturePath } elseif ($AssetName.StartsWith('latest.json')) { $finalManifestPath } elseif ($AssetName.EndsWith('.sha256')) { $bundle.ChecksumPath } else { $archive }
            Copy-Item -LiteralPath $source -Destination $Destination
        }
        function Invoke-ReleaseHttp { return @{StatusCode=200;Content='{"id":123,"tag_name":"v1.2.2","prerelease":false}'} }
        Assert-Fails { Assert-PublicRelease } 'wrong version'
        function Invoke-ReleaseHttp { return @{StatusCode=200;Content='{"id":123,"tag_name":"v1.2.3","prerelease":false}'} }
        function Get-GiteeAttachments { return @(@{name=$expectedArchiveName},@{name=$expectedArchiveName}) }
        Assert-Fails { Assert-PublicRelease } '2 attachments'
        function Get-GiteeAttachments { return @() }
        Assert-Fails { Assert-PublicRelease } '0 attachments'
    }
    & {
        . $publisherFunctions
        Assert-Fails { Assert-ArchiveContents $archive } 'integrity test failed'
        $incomplete = Join-Path $tempRoot 'incomplete.7z'
        $incompleteRoot = Join-Path $tempRoot 'incomplete-payload'
        $incompleteProduct = Join-Path $incompleteRoot 'NetBootDhcpTool'
        New-Item -ItemType Directory -Path $incompleteProduct -Force | Out-Null
        Copy-Item -LiteralPath $finalManifestPath -Destination (Join-Path $incompleteProduct 'latest.json')
        Push-Location $incompleteRoot
        try {
            & 'C:\Program Files\7-Zip\7z.exe' a $incomplete 'NetBootDhcpTool' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Archive fixture failed' }
        } finally { Pop-Location }
        Assert-Fails { Assert-ArchiveContents $incomplete } 'archive is incomplete'
    }
    Write-Output 'RELEASE_RESILIENCE_TESTS_OK'
    $global:LASTEXITCODE = 0
} finally {
    $env:GITEE_TOKEN = $savedToken
    $resolved = [IO.Path]::GetFullPath($tempRoot)
    $parent = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolved) -cne $parent -or -not [IO.Path]::GetFileName($resolved).StartsWith('netboot-resilience-')) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
