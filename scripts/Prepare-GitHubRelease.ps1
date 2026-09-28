[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$GitHubRepository = 'shashouaq/NetBootDhcpTool'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$appProject = Join-Path $root 'src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj'
[xml]$project = Get-Content -LiteralPath $appProject
$version = [string]$project.Project.PropertyGroup.Version
if ($Tag -cne "v$version") { throw "Release tag $Tag does not match application version $version." }
if (Test-Path -LiteralPath $output) { throw "Release output already exists; refusing to rebuild over it: $output" }
if ($GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'GitHubRepository must use owner/repository form.' }
if ([string]::IsNullOrWhiteSpace($env:NETBOOT_UPDATE_SIGNING_PRIVATE_KEY)) {
    throw 'NETBOOT_UPDATE_SIGNING_PRIVATE_KEY is required to sign the automatic update manifest.'
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP must be set by GitHub Actions.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null

$baseline = Join-Path $env:RUNNER_TEMP ("netboot-release-baseline-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $baseline -Force | Out-Null
$baselineManifest = $null
try {
    $apiHeaders = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'NetBootDhcpTool-FormalRelease/1.0' }
    $latestUri = "https://api.github.com/repos/$GitHubRepository/releases/latest"
    $latestResponse = Invoke-WebRequest -Uri $latestUri -Headers $apiHeaders -SkipHttpErrorCheck -TimeoutSec 30 -ErrorAction Stop
    if ($latestResponse.StatusCode -eq 200) {
        $latest = $latestResponse.Content | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        if ([string]$latest.tag_name -match '^v\d+\.\d+\.\d+$' -and [version]$latest.tag_name.Substring(1) -lt [version]$version) {
            $fullAssets = @($latest.assets | Where-Object { $_.name -ceq "NetBootDhcpTool-full-v$($latest.tag_name.Substring(1)).zip" })
            if ($fullAssets.Count -eq 1) {
                $fullPath = Join-Path $env:RUNNER_TEMP ("netboot-previous-full-" + [guid]::NewGuid().ToString('N') + '.zip')
                try {
                    $download = Invoke-WebRequest -Uri ([string]$fullAssets[0].browser_download_url) -Headers @{ 'User-Agent' = 'NetBootDhcpTool-FormalRelease/1.0' } -OutFile $fullPath -PassThru -TimeoutSec 180 -ErrorAction Stop
                    if ([int]$download.StatusCode -ne 200 -or [long](Get-Item -LiteralPath $fullPath).Length -ne [long]$fullAssets[0].size) {
                        throw 'The prior stable Full package could not be read back at its declared size.'
                    }
                    $zip = [System.IO.Compression.ZipFile]::OpenRead($fullPath)
                    try {
                        $entry = $zip.GetEntry('payload/install-manifest.json')
                        if ($null -eq $entry) { throw 'The prior Full package has no installation manifest.' }
                        $manifestOutput = Join-Path $baseline 'prior-install-manifest.json'
                        $input = $entry.Open()
                        $stream = [System.IO.File]::Create($manifestOutput)
                        try { $input.CopyTo($stream) } finally { $stream.Dispose(); $input.Dispose() }
                    } finally { $zip.Dispose() }
                    $baselineManifest = Get-Content -LiteralPath $manifestOutput -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
                    if ($baselineManifest.productId -cne 'NetBootDhcpTool' -or
                        [string]$baselineManifest.version -cne $latest.tag_name.Substring(1) -or
                        @($baselineManifest.files).Count -eq 0) {
                        throw 'The prior Full package contains an invalid or mismatched installation manifest.'
                    }
                    $baselineManifestPath = Join-Path (Join-Path $baseline "NetBootDhcpTool-$($latest.tag_name)") 'install-manifest.json'
                    New-Item -ItemType Directory -Path (Split-Path -Parent $baselineManifestPath) -Force | Out-Null
                    Move-Item -LiteralPath $manifestOutput -Destination $baselineManifestPath
                    Write-Host "Using signed release baseline $($latest.tag_name) for OTA comparison."
                } finally { Remove-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue }
            } else {
                Write-Host "Latest stable release $($latest.tag_name) has no Full update asset; the first signed-updater release will be Full-only."
            }
        }
    } elseif ($latestResponse.StatusCode -ne 404) {
        throw "GitHub latest Release lookup returned HTTP $($latestResponse.StatusCode); refusing an unverified OTA baseline."
    }

    $buildOutput = Join-Path $env:RUNNER_TEMP ('netboot-release-build-' + [guid]::NewGuid().ToString('N'))
    try {
        & (Join-Path $root 'build\publish.ps1') -GitHubRepository $GitHubRepository -OutputDirectory $buildOutput -BaselineDirectory $baseline
        if ($LASTEXITCODE -ne 0) { throw "Portable package build failed with exit code $LASTEXITCODE." }
        Import-Module (Join-Path $root 'build\release-pipeline\ReleaseState.psm1') -Force
        $bundle = Get-ReleaseBundle -Directory $buildOutput -Tag $Tag
        if ($bundle.Version -cne $version) { throw 'Built release bundle version does not match the requested tag.' }
        $giteeBase = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/$Tag"
        $githubBase = "https://github.com/$GitHubRepository/releases/download/$Tag"
        $bundle.Manifest.downloadUrl = "$giteeBase/$($bundle.ArchiveName)"
        $bundle.Manifest.downloadMirrors = @("$githubBase/$($bundle.ArchiveName)")
        $bundle.Manifest.releasePageUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/$Tag"
        foreach ($package in @($bundle.Manifest.packages)) {
            $package.downloadUrl = "$giteeBase/$($package.fileName)"
            $package.downloadMirrors = @("$githubBase/$($package.fileName)")
        }
        $manifestPath = Join-Path $buildOutput 'latest.json'
        $manifestJson = ConvertTo-Json -InputObject $bundle.Manifest -Depth 12
        $manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($manifestJson)
        [System.IO.File]::WriteAllBytes($manifestPath, $manifestBytes)

        $rsa = [System.Security.Cryptography.RSA]::Create()
        try {
            $privatePem = $env:NETBOOT_UPDATE_SIGNING_PRIVATE_KEY
            $rsa.ImportFromPem($privatePem)
            if ($rsa.KeySize -lt 3072) { throw 'The update signing key must be at least RSA 3072-bit.' }
            $clientSource = Get-Content -LiteralPath (Join-Path $root 'src\NetBootDhcpTool.Core\UpdatePackages.cs') -Raw
            $publicMatch = [regex]::Match($clientSource, '(?ms)public const string TrustedPublicKeyPem = """\r?\n(?<pem>.*?)\r?\n""";')
            if (-not $publicMatch.Success) { throw 'Could not read the client trusted update key.' }
            $trusted = [System.Security.Cryptography.RSA]::Create()
            try {
                $trusted.ImportFromPem($publicMatch.Groups['pem'].Value)
                $privatePublic = $rsa.ExportParameters($false)
                $trustedPublic = $trusted.ExportParameters($false)
                if ([Convert]::ToBase64String($privatePublic.Modulus) -cne [Convert]::ToBase64String($trustedPublic.Modulus) -or
                    [Convert]::ToBase64String($privatePublic.Exponent) -cne [Convert]::ToBase64String($trustedPublic.Exponent)) {
                    throw 'The update signing key does not match the public key trusted by the client.'
                }
            } finally { $trusted.Dispose() }
            $signature = $rsa.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
            [System.IO.File]::WriteAllText((Join-Path $buildOutput 'latest.json.sig'), [Convert]::ToBase64String($signature) + [System.Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        } finally {
            $rsa.Dispose()
            [System.Environment]::SetEnvironmentVariable('NETBOOT_UPDATE_SIGNING_PRIVATE_KEY', $null, 'Process')
            $env:NETBOOT_UPDATE_SIGNING_PRIVATE_KEY = ''
        }

        $sevenZip = 'C:\Program Files\7-Zip\7z.exe'
        if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) { throw '7-Zip is required to verify the release archives on the GitHub-hosted runner.' }
        $assets = @($bundle.AssetFiles) + @($manifestPath, (Join-Path $buildOutput 'latest.json.sig'))
        foreach ($assetPath in $bundle.AssetFiles | Where-Object { $_.EndsWith('.7z', [System.StringComparison]::OrdinalIgnoreCase) -or $_.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase) }) {
            & $sevenZip t $assetPath
            if ($LASTEXITCODE -ne 0) { throw "Archive integrity check failed for $(Split-Path -Leaf $assetPath)." }
        }
        foreach ($asset in $assets) { Copy-Item -LiteralPath $asset -Destination $output }
        $assetFiles = @(Get-ChildItem -LiteralPath $output -File)
        if ($assetFiles.Count -ne $assets.Count) { throw 'Prepared release asset count does not match the validated local bundle.' }
        foreach ($asset in $assetFiles) {
            Write-Host "Prepared $($asset.Name): $($asset.Length) bytes, SHA-256 $((Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant())."
        }
        Write-Host "Prepared one signed GitHub release package set for $Tag."
    } finally {
        Remove-Item -LiteralPath $buildOutput -Recurse -Force -ErrorAction SilentlyContinue
    }
} finally {
    Remove-Item -LiteralPath $baseline -Recurse -Force -ErrorAction SilentlyContinue
}
