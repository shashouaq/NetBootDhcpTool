[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$MakensisPath,
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $output -PathType Container)) { throw "Setup asset directory does not exist: $output" }

$manifestPath = Join-Path $output 'latest-v2.json'
$signaturePath = Join-Path $output 'latest-v2.json.sig'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
    throw 'Signed latest-v2.json and its signature are required before building Setup.exe.'
}
$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json -AsHashtable -ErrorAction Stop
if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'The V2 manifest version is invalid.' }
$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
$signatureBytes = [Convert]::FromBase64String(([System.IO.File]::ReadAllText($signaturePath)).Trim())
$coreSource = [System.IO.File]::ReadAllText((Join-Path $root 'src\NetBootDhcpTool.Core\UpdatePackages.cs'))
$publicMatch = [regex]::Match($coreSource, '(?ms)public const string TrustedPublicKeyPem = """\r?\n(?<pem>.*?)\r?\n""";')
if (-not $publicMatch.Success) { throw 'Could not load the trusted update public key.' }
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($publicMatch.Groups['pem'].Value)
    if (-not $rsa.VerifyData($manifestBytes, $signatureBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'The V2 manifest signature is invalid.'
    }
} finally { $rsa.Dispose() }
$version = [string]$manifest.version
$fullPackages = @($manifest.sevenZipPackages | Where-Object { $_.kind -ceq 'Full' })
if ($fullPackages.Count -ne 1) { throw 'The signed V2 manifest must contain exactly one Full 7z package.' }
$fullPackage = $fullPackages[0]
$fullPath = Join-Path $output ([string]$fullPackage.fileName)
if ([System.IO.Path]::GetFileName([string]$fullPackage.fileName) -cne [string]$fullPackage.fileName -or -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw 'The signed Full 7z package is missing or has an unsafe filename.' }
$fullInfo = Get-Item -LiteralPath $fullPath
$fullHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($fullInfo.Length -ne [long]$fullPackage.size -or $fullHash -cne ([string]$fullPackage.sha256).ToLowerInvariant()) {
    throw 'The Full 7z package size or SHA-256 does not match the signed V2 manifest.'
}
$sidecar = ([System.IO.File]::ReadAllText("$fullPath.sha256")).Trim() -split '\s+', 2
if ($sidecar.Count -ne 2 -or $sidecar[0] -cne $fullHash -or $sidecar[1] -cne [string]$fullPackage.fileName) {
    throw 'The Full 7z SHA-256 sidecar is invalid.'
}

if ([string]::IsNullOrWhiteSpace($MakensisPath)) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { $MakensisPath = $command.Source }
}
if ([string]::IsNullOrWhiteSpace($MakensisPath) -or -not (Test-Path -LiteralPath $MakensisPath -PathType Leaf)) {
    throw 'makensis.exe was not found. Install the pinned NSIS 3.12 compiler in the build environment.'
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('NetBootDhcpTool-SetupBuild-' + [guid]::NewGuid().ToString('N'))
$helperOutput = Join-Path $tempRoot 'helper'
$helperPath = Join-Path $helperOutput 'NetBootDhcpTool.SetupHelper.exe'
$setupPath = Join-Path $output "NetBootDhcpTool-Setup-v$version.exe"
New-Item -ItemType Directory -Path $helperOutput -Force | Out-Null
try {
    & $DotnetPath publish (Join-Path $root 'src\NetBootDhcpTool.SetupHelper\NetBootDhcpTool.SetupHelper.csproj') `
        -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -o $helperOutput
    if ($LASTEXITCODE -ne 0) { throw "Setup helper publish failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) { throw 'The self-contained setup helper was not produced.' }

    $script = Join-Path $root 'installer\NetBootDhcpTool.Setup.nsi'
    $resultSource = [System.IO.File]::ReadAllText((Join-Path $root 'src\NetBootDhcpTool.Core\UpdateResultProtocol.cs'))
    $cancelCode = [regex]::Match($resultSource, 'Cancelled\s*=\s*(\d+)').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($cancelCode)) { throw 'Cannot read the shared user-cancel exit code.' }
    Push-Location $root
    try {
        & $MakensisPath "-DPRODUCT_VERSION=$version" "-DSETUP_HELPER=$helperPath" "-DMANIFEST=$manifestPath" `
            "-DSIGNATURE=$signaturePath" "-DFULL_PACKAGE_NAME=$($fullPackage.fileName)" "-DOUTPUT_DIRECTORY=$output" "-DUSER_CANCELLED_EXIT_CODE=$cancelCode" $script
        if ($LASTEXITCODE -ne 0) { throw "NSIS packaging failed with exit code $LASTEXITCODE." }
    } finally { Pop-Location }

    $setupInfo = Get-Item -LiteralPath $setupPath
    $combinedSize = $setupInfo.Length + $fullInfo.Length
    $warningThreshold = 95000000L
    foreach ($asset in @(
        [pscustomobject]@{ Name = 'Setup.exe'; Size = [long]$setupInfo.Length }
        [pscustomobject]@{ Name = 'Full 7z'; Size = [long]$fullInfo.Length }
    )) {
        if ($asset.Size -gt $warningThreshold) {
            Write-Warning "Individual asset $($asset.Name) is $($asset.Size) bytes, above the 95,000,000-byte Gitee risk threshold. No authoritative Release-asset hard limit is configured."
        } elseif ($asset.Size -ge 80000000L) {
            Write-Warning "Individual asset $($asset.Name) is in the 80–95 MB risk band: $($asset.Size) bytes."
        }
    }
    Write-Host "Setup.exe: $($setupInfo.Length) bytes; SHA-256 $((Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant())."
    Write-Host "Paired Full 7z: $($fullInfo.Length) bytes; SHA-256 $fullHash."
    Write-Host "First migration total download: $combinedSize bytes across two independently downloadable assets (UX/bandwidth metric only; not a platform size gate)."
} finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
