[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$GitHubRepository = 'shashouaq/NetBootDhcpTool',
    [switch]$ReleaseCandidate
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $root 'build\release-identity.ps1')
$identity = Get-NetBootReleaseIdentity -Tag $Tag -ReleaseCandidate:$ReleaseCandidate
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$appProject = Join-Path $root 'src\NetBootDhcpTool.App\NetBootDhcpTool.App.csproj'
$version = (Get-NetBootVersionMetadata -RepositoryRoot $root).Version
if ($identity.Version -cne $version) { throw "Release tag $Tag does not match application version $version." }
if (Test-Path -LiteralPath $output) { throw "Release output already exists; refusing to rebuild over it: $output" }
if ($GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'GitHubRepository must use owner/repository form.' }
if ([string]::IsNullOrWhiteSpace($env:NETBOOT_UPDATE_SIGNING_PRIVATE_KEY)) { throw 'NETBOOT_UPDATE_SIGNING_PRIVATE_KEY is required to sign latest-v2.json.' }
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP must be set by GitHub Actions.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null

$buildOutput = Join-Path $env:RUNNER_TEMP ('netboot-release-build-' + [guid]::NewGuid().ToString('N'))
try {
    & (Join-Path $root 'build\publish.ps1') -GitHubRepository $GitHubRepository -OutputDirectory $buildOutput
    if ($LASTEXITCODE -ne 0) { throw "Package build failed with exit code $LASTEXITCODE." }
    Import-Module (Join-Path $root 'build\release-pipeline\ReleaseState.psm1') -Force
    $bundle = Get-ReleaseBundle -Directory $buildOutput -Tag "v$version"
    if ($bundle.Version -cne $version) { throw 'Built release bundle version does not match the requested tag.' }

    $giteeBase = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/$Tag"
    $githubBase = "https://github.com/$GitHubRepository/releases/download/$Tag"
    $bundle.Manifest.downloadUrl = "$giteeBase/$($bundle.ArchiveName)"
    $bundle.Manifest.downloadMirrors = @("$githubBase/$($bundle.ArchiveName)")
    $bundle.Manifest.releasePageUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/$Tag"
    foreach ($package in @($bundle.Manifest.packages) + @($bundle.Manifest.sevenZipPackages)) {
        $package.downloadUrl = "$giteeBase/$($package.fileName)"
        $package.downloadMirrors = @("$githubBase/$($package.fileName)")
    }
    $manifestPath = Join-Path $buildOutput 'latest-v2.json'
    $signaturePath = Join-Path $buildOutput 'latest-v2.json.sig'
    $manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $bundle.Manifest -Depth 12))
    [System.IO.File]::WriteAllBytes($manifestPath, $manifestBytes)

    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($env:NETBOOT_UPDATE_SIGNING_PRIVATE_KEY)
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
        [System.IO.File]::WriteAllText($signaturePath, [Convert]::ToBase64String($signature) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    } finally {
        $rsa.Dispose()
        [System.Environment]::SetEnvironmentVariable('NETBOOT_UPDATE_SIGNING_PRIVATE_KEY', $null, 'Process')
        $env:NETBOOT_UPDATE_SIGNING_PRIVATE_KEY = ''
    }

    $dotnet = & (Join-Path $root 'build\resolve-dotnet.ps1')
    $makensis = if (-not [string]::IsNullOrWhiteSpace($env:MAKENSIS_PATH)) { $env:MAKENSIS_PATH } else { 'makensis.exe' }
    & (Join-Path $root 'build\package-setup.ps1') -OutputDirectory $buildOutput -MakensisPath $makensis -DotnetPath $dotnet
    if ($LASTEXITCODE -ne 0) { throw "Setup packaging failed with exit code $LASTEXITCODE." }
    . (Join-Path $root 'build\pe-version.ps1')
    Assert-NetBootReleaseVersions -Directory $buildOutput -Version $version

    $sevenZip = 'C:\Program Files\7-Zip\7z.exe'
    if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) { throw '7-Zip is required to verify the release archives on the GitHub-hosted runner.' }
    foreach ($assetPath in $bundle.AssetFiles | Where-Object { $_.EndsWith('.7z', [System.StringComparison]::OrdinalIgnoreCase) }) {
        & $sevenZip t $assetPath
        if ($LASTEXITCODE -ne 0) { throw "Archive integrity check failed for $(Split-Path -Leaf $assetPath)." }
    }

    $legacyRoot = Join-Path $root 'build\legacy\v1.0.20'
    $legacyManifest = Join-Path $legacyRoot 'latest.json'
    $legacySignature = Join-Path $legacyRoot 'latest.json.sig'
    foreach ($legacyFile in @($legacyManifest, $legacySignature)) {
        if (-not (Test-Path -LiteralPath $legacyFile -PathType Leaf)) { throw 'Byte-preserved v1.0.20 legacy manifest assets are missing.' }
    }
    $setupPath = Join-Path $buildOutput "NetBootDhcpTool-Setup-v$version.exe"
    . (Join-Path $root 'build\legacy-manifest.ps1')
    Copy-NetBootFrozenLegacyManifest -RepositoryRoot $root -Destination $output
    $assets = @($bundle.AssetFiles) + @($manifestPath, $signaturePath, $setupPath)
    foreach ($asset in $assets) { Copy-Item -LiteralPath $asset -Destination $output }
    $assetFiles = @(Get-ChildItem -LiteralPath $output -File)
    if ($assetFiles.Count -ne ($assets.Count + 2)) { throw 'Prepared release asset count does not match the validated local bundle.' }
    foreach ($asset in $assetFiles) {
        Write-Host "Prepared $($asset.Name): $($asset.Length) bytes, SHA-256 $((Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant())."
    }
    Write-Host "Prepared the V2 Full 7z update and paired full installer for $Tag."
} finally {
    Remove-Item -LiteralPath $buildOutput -Recurse -Force -ErrorAction SilentlyContinue
}
