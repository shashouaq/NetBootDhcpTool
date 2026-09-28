[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [string]$GitHubRepository = 'shashouaq/NetBootDhcpTool'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN is required for GitHub Release publication.' }
if ($GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'GitHubRepository must use owner/repository form.' }
$assetRoot = [System.IO.Path]::GetFullPath($AssetDirectory)
if (-not (Test-Path -LiteralPath $assetRoot -PathType Container)) { throw "Release artifact directory does not exist: $assetRoot" }
$version = $Tag.Substring(1)
$manifestPath = Join-Path $assetRoot 'latest.json'
$signaturePath = Join-Path $assetRoot 'latest.json.sig'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
    throw 'Build artifact must contain latest.json and latest.json.sig.'
}
$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json -AsHashtable -ErrorAction Stop
if ([string]$manifest.version -cne $version -or [string]$manifest.archiveName -cne "NetBootDhcpTool-v$version.7z" -or
    [string]$manifest.archiveSha256 -notmatch '^[a-fA-F0-9]{64}$') {
    throw 'Build artifact latest.json does not match the requested tag.'
}
$packages = @($manifest.packages)
if ($packages.Count -lt 1 -or $packages.Count -gt 8 -or @($packages | Where-Object { $_.kind -ceq 'Full' }).Count -ne 1) {
    throw 'Build artifact latest.json must contain exactly one Full package and no more than eight packages.'
}
foreach ($package in $packages) {
    if ([string]$package.kind -ceq 'Full') {
        if ([string]$package.fileName -cne "NetBootDhcpTool-full-v$version.zip") { throw 'Full package filename does not match the requested tag.' }
    } elseif ([string]$package.kind -ceq 'Ota') {
        if ([string]$package.fileName -notmatch "^NetBootDhcpTool-ota-v\d+\.\d+\.\d+-to-v$([regex]::Escape($version))\.zip$" -or
            [string]$package.baseVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$package.baseVersion -ge [version]$version) {
            throw 'OTA package filename or base version does not match the requested tag.'
        }
    } else {
        throw 'Build artifact package kind must be Full or Ota.'
    }
}

$signatureText = [System.IO.File]::ReadAllText($signaturePath)
$publicSource = [System.IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'src\NetBootDhcpTool.Core\UpdatePackages.cs'))
$publicMatch = [regex]::Match($publicSource, '(?ms)public const string TrustedPublicKeyPem = """\r?\n(?<pem>.*?)\r?\n""";')
if (-not $publicMatch.Success) { throw 'Could not read the client trusted update key.' }
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($publicMatch.Groups['pem'].Value)
    $signature = [Convert]::FromBase64String($signatureText.Trim())
    if (-not $rsa.VerifyData($manifestBytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'Build artifact latest.json signature is invalid.'
    }
} finally { $rsa.Dispose() }

$assetNames = [System.Collections.Generic.List[string]]::new()
$assetNames.Add([string]$manifest.archiveName)
$assetNames.Add(([string]$manifest.archiveName + '.sha256'))
foreach ($package in @($manifest.packages)) {
    if ([string]$package.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$package.size -le 0) {
        throw 'Build artifact package metadata is invalid.'
    }
    $assetNames.Add([string]$package.fileName)
    $assetNames.Add(([string]$package.fileName + '.sha256'))
}
$assetNames.Add('latest.json.sig')
$assetNames.Add('latest.json')
if ($assetNames.Count -ne @($assetNames | Select-Object -Unique).Count) { throw 'Build artifact contains duplicate expected asset names.' }
$localAssets = [ordered]@{}
$actualAssetNames = @(Get-ChildItem -LiteralPath $assetRoot -File | ForEach-Object { $_.Name })
if ($actualAssetNames.Count -ne $assetNames.Count -or @($actualAssetNames | Where-Object { $_ -cnotin $assetNames }).Count -gt 0) {
    throw 'Build artifact contains missing or undeclared release assets.'
}
foreach ($name in $assetNames) {
    $path = Join-Path $assetRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Build artifact is missing $name." }
    $localAssets[$name] = [pscustomobject]@{ Path = $path; Size = [long](Get-Item -LiteralPath $path).Length; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
if ($localAssets[$manifest.archiveName].Sha256 -cne ([string]$manifest.archiveSha256).ToLowerInvariant()) { throw 'Portable archive SHA-256 does not match latest.json.' }

function Assert-ReleaseSidecar([string]$Name, [string]$ExpectedHash) {
    $sidecarName = "$Name.sha256"
    $parts = ([System.IO.File]::ReadAllText($localAssets[$sidecarName].Path)).Trim() -split '\s+', 2
    if ($parts.Count -ne 2 -or $parts[0] -cne $ExpectedHash -or $parts[1] -cne $Name) {
        throw "SHA-256 sidecar content is invalid for $Name."
    }
}

Assert-ReleaseSidecar $manifest.archiveName $localAssets[$manifest.archiveName].Sha256
foreach ($package in $packages) {
    $record = $localAssets[[string]$package.fileName]
    if ($record.Sha256 -cne ([string]$package.sha256).ToLowerInvariant() -or $record.Size -ne [long]$package.size) {
        throw "Update package does not match signed manifest metadata: $($package.fileName)."
    }
    Assert-ReleaseSidecar ([string]$package.fileName) $record.Sha256
}

function Get-GitHubHeaders([string]$Accept = 'application/vnd.github+json') {
    @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = $Accept; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'NetBootDhcpTool-FormalRelease/1.0' }
}

function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [ValidateSet('Get', 'Post', 'Patch')][string]$Method = 'Get',
        [string]$Body,
        [string]$OutFile,
        [string]$InFile,
        [string]$ContentType = 'application/json; charset=utf-8',
        [string]$Accept = 'application/vnd.github+json'
    )
    $parameters = @{ Uri = $Uri; Method = $Method; Headers = (Get-GitHubHeaders $Accept); SkipHttpErrorCheck = $true; MaximumRedirection = 10; TimeoutSec = 180; ErrorAction = 'Stop' }
    if ($Body) { $parameters.Body = $Body; $parameters.ContentType = $ContentType }
    if ($OutFile) { $parameters.OutFile = $OutFile; $parameters.PassThru = $true }
    if ($InFile) { $parameters.InFile = $InFile; $parameters.ContentType = $ContentType }
    try {
        $response = Invoke-WebRequest @parameters
        $content = if ($OutFile) { '' } else { [string]$response.Content }
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = $content }
    } catch {
        $hostName = ([uri]$Uri).Host
        throw "GitHub HTTPS $Method failed at $hostName ($($_.Exception.GetType().Name))."
    }
}

function Convert-GitHubJson([string]$Content, [string]$Context) {
    if ([string]::IsNullOrWhiteSpace($Content)) { throw "$Context returned an empty response." }
    try { return ConvertFrom-Json -InputObject $Content -AsHashtable -ErrorAction Stop }
    catch { throw "$Context returned invalid JSON." }
}

function Get-GitHubRelease {
    $uri = "https://api.github.com/repos/$GitHubRepository/releases/tags/$([uri]::EscapeDataString($Tag))"
    $response = Invoke-GitHubApi -Uri $uri
    if ($response.StatusCode -eq 404) { return $null }
    if ($response.StatusCode -ne 200) { throw "GitHub Release lookup returned HTTP $($response.StatusCode)." }
    $release = Convert-GitHubJson $response.Content 'GitHub Release lookup'
    if ([string]$release.tag_name -cne $Tag) { throw 'GitHub returned a Release with a different tag.' }
    return $release
}

function Get-GitHubReleaseAssets([object]$Release) {
    $duplicateNames = @($Release.assets | Group-Object -Property name | Where-Object Count -gt 1)
    if ($duplicateNames.Count -gt 0) { throw 'GitHub Release contains duplicate attachment names.' }
    $actual = @($Release.assets | ForEach-Object { [string]$_.name })
    foreach ($name in $actual) {
        if ($name -cnotin $assetNames) { throw "GitHub Release contains an undeclared asset: $name." }
    }
    return @($Release.assets)
}

function Test-GitHubAssetReadback([object]$Asset, [string]$Name) {
    $local = $localAssets[$Name]
    if ([long]$Asset.size -ne $local.Size) { throw "GitHub already has a different-sized $Name; refusing to overwrite it." }
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('netboot-gh-readback-' + [guid]::NewGuid().ToString('N') + '-' + $Name)
    try {
        $response = Invoke-GitHubApi -Uri ([string]$Asset.browser_download_url) -OutFile $path -Accept 'application/octet-stream'
        if ($response.StatusCode -ne 200 -or -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $local.Sha256) {
            throw "GitHub asset full download SHA-256 mismatch for $Name; existing bytes are preserved."
        }
        if ([long](Get-Item -LiteralPath $path).Length -ne $local.Size) { throw "GitHub full download size mismatch for $Name." }
    } finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

function Get-AssetByName([object]$Release, [string]$Name) {
    $matches = @(Get-GitHubReleaseAssets $Release | Where-Object { [string]$_.name -ceq $Name })
    if ($matches.Count -gt 1) { throw "GitHub has duplicate assets named $Name." }
    if ($matches.Count -eq 0) { return $null }
    return $matches[0]
}

function Ensure-GitHubAsset([object]$Release, [string]$Name) {
    $asset = Get-AssetByName $Release $Name
    if ($null -ne $asset) {
        Test-GitHubAssetReadback $asset $Name
        Write-Host "[GitHub] Reused and verified $Name."
        return $Release
    }
    $uploadUri = "https://uploads.github.com/repos/$GitHubRepository/releases/$($Release.id)/assets?name=$([uri]::EscapeDataString($Name))"
    $upload = Invoke-GitHubApi -Uri $uploadUri -Method Post -InFile $localAssets[$Name].Path -ContentType 'application/octet-stream'
    $Release = Get-GitHubRelease
    $asset = if ($null -eq $Release) { $null } else { Get-AssetByName $Release $Name }
    if ($null -eq $asset) {
        throw "GitHub upload for $Name returned HTTP $($upload.StatusCode), and no asset is visible. Rerun the failed publish job to reconcile the same build artifact."
    }
    Test-GitHubAssetReadback $asset $Name
    Write-Host "[GitHub] Uploaded and verified $Name."
    return $Release
}

$release = Get-GitHubRelease
if ($null -eq $release) {
    $payload = [ordered]@{
        tag_name = $Tag
        name = "NetBoot DHCP Tool $Tag"
        body = [string]$manifest.releaseNotes
        draft = $false
        prerelease = $true
        make_latest = 'false'
    }
    $create = Invoke-GitHubApi -Uri "https://api.github.com/repos/$GitHubRepository/releases" -Method Post -Body (ConvertTo-Json -InputObject $payload -Depth 8 -Compress)
    if ($create.StatusCode -notin @(200, 201)) {
        $release = Get-GitHubRelease
        if ($null -eq $release) { throw "GitHub prerelease creation returned HTTP $($create.StatusCode); no Release for $Tag was confirmed." }
        Write-Host '[GitHub] Recovered the Release after an ambiguous create response.'
    } else {
        $release = Convert-GitHubJson $create.Content 'GitHub Release creation'
        Write-Host "[GitHub] Created prerelease $Tag."
    }
}
if ([bool]$release.draft) {
    $activate = Invoke-GitHubApi -Uri "https://api.github.com/repos/$GitHubRepository/releases/$($release.id)" -Method Patch -Body (ConvertTo-Json -InputObject @{ draft = $false; prerelease = $true; make_latest = 'false' } -Compress)
    if ($activate.StatusCode -notin @(200, 201)) { throw "GitHub draft recovery returned HTTP $($activate.StatusCode)." }
    $release = Convert-GitHubJson $activate.Content 'GitHub draft recovery'
}

foreach ($name in $assetNames) { $release = Ensure-GitHubAsset $release $name }
$release = Get-GitHubRelease
if ($null -eq $release) { throw "GitHub Release $Tag disappeared after asset publication." }
foreach ($name in $assetNames) {
    $asset = Get-AssetByName $release $name
    if ($null -eq $asset) { throw "GitHub Release final inventory is missing $name." }
}

if ([bool]$release.prerelease) {
    $stable = Invoke-GitHubApi -Uri "https://api.github.com/repos/$GitHubRepository/releases/$($release.id)" -Method Patch -Body (ConvertTo-Json -InputObject @{ prerelease = $false; draft = $false; make_latest = 'true' } -Compress)
    if ($stable.StatusCode -notin @(200, 201)) { throw "GitHub stable Release promotion returned HTTP $($stable.StatusCode)." }
}
$release = Get-GitHubRelease
if ($null -eq $release -or [bool]$release.draft -or [bool]$release.prerelease) { throw "GitHub Release $Tag is not stable after promotion." }

$fullPackage = @($manifest.packages | Where-Object { $_.kind -ceq 'Full' })[0]
$summary = @(
    "## GitHub Formal Release SUCCESS",
    '',
    "- Tag: $Tag",
    "- Stable Release: https://github.com/$GitHubRepository/releases/tag/$Tag",
    "- Assets: $($assetNames.Count)",
    "- Portable archive SHA-256: $($localAssets[$manifest.archiveName].Sha256)",
    "- Full package SHA-256: $($localAssets[[string]$fullPackage.fileName].Sha256)",
    '- Every asset was downloaded from the GitHub Release API/URL and matched its local SHA-256 before promotion.'
) -join [System.Environment]::NewLine
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
