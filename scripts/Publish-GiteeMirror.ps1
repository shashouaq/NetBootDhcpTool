[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$GitHubRepository = 'shashouaq/NetBootDhcpTool',
    [string]$GiteeOwner = 'joel20230302',
    [string]$GiteeRepository = 'NetBootDhcpTool',
    [string]$AssetDirectory,
    [ValidateRange(30, 300)][int]$UploadTimeoutSec = 180,
    [string]$TelemetryPath,
    [string]$CredentialPath,
    [switch]$LibraryOnly,
    [switch]$ReleaseCandidate
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:MirrorRoot = Split-Path -Parent $PSScriptRoot
$script:RepositoryRoot = $script:MirrorRoot
. (Join-Path $PSScriptRoot 'Get-GiteeCredential.ps1') -LoadOnly
. (Join-Path $script:MirrorRoot 'build\release-identity.ps1')
$null = Get-NetBootReleaseIdentity -Tag $Tag -ReleaseCandidate:$ReleaseCandidate
$script:MirrorIsCandidate = [bool]$ReleaseCandidate
$script:GiteeAssetWarningThresholdBytes = 95000000L
$script:GiteeReleaseAssetMaxBytes = $null

function Assert-GiteeAssetSizePreflight {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][long]$Size
    )

    if ($Size -lt 0) { throw 'Gitee asset size must be a nonnegative byte count.' }
    if ($null -ne $script:GiteeReleaseAssetMaxBytes) {
        $maximum = [long]$script:GiteeReleaseAssetMaxBytes
        if ($maximum -le 0) { throw 'GiteeReleaseAssetMaxBytes must be a positive byte count when configured.' }
        if ($Size -ge $maximum) {
            throw "Gitee asset size preflight rejected $AssetName before upload: size=$Size bytes maximum=$maximum bytes."
        }
    }

    if ($Size -gt $script:GiteeAssetWarningThresholdBytes) {
        Write-Warning "Gitee asset $AssetName is $Size bytes, above the $($script:GiteeAssetWarningThresholdBytes)-byte warning threshold."
    }
}

function Assert-GiteeAssetSetSizePreflight([System.Collections.IDictionary]$Assets) {
    foreach ($entry in $Assets.GetEnumerator()) {
        Assert-GiteeAssetSizePreflight -AssetName ([string]$entry.Key) -Size ([long]$entry.Value.Size)
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
        [string]$UploadFile,
        [hashtable]$FormFields = @{},
        [ValidateRange(1, 300)][int]$TimeoutSec = 45
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
            $uploadTimer = [System.Diagnostics.Stopwatch]::StartNew()
            $curlOutput = $curlConfig | & $curl --disable --config - --silent --show-error --http1.1 --header 'Expect:' --connect-timeout 20 --max-time $TimeoutSec --fail-with-body --request POST --form "file=@$UploadFile;filename=$([System.IO.Path]::GetFileName($UploadFile))" --dump-header $headersPath --output $responsePath --write-out 'http=%{http_code} seconds=%{time_total} uploaded=%{size_upload} speed=%{speed_upload}' $Uri 2>&1
            $uploadTimer.Stop()
            $exitCode = $LASTEXITCODE
            $stats = ($curlOutput | Out-String).Trim()
            $statusMatch = [regex]::Match($stats, '(?:^|\s)http=(?<status>\d{3})(?:\s|$)')
            $secondsMatch = [regex]::Match($stats, '(?:^|\s)seconds=(?<seconds>\d+(?:\.\d+)?)(?:\s|$)')
            $uploadedMatch = [regex]::Match($stats, '(?:^|\s)uploaded=(?<bytes>\d+)(?:\s|$)')
            $speedMatch = [regex]::Match($stats, '(?:^|\s)speed=(?<bytesPerSecond>\d+)(?:\s|$)')
            $statusCode = if ($statusMatch.Success) { [int]$statusMatch.Groups['status'].Value } else { 0 }
            $transferSeconds = if ($secondsMatch.Success) { [double]::Parse($secondsMatch.Groups['seconds'].Value, [Globalization.CultureInfo]::InvariantCulture) } else { [math]::Round($uploadTimer.Elapsed.TotalSeconds, 3) }
            $uploadedBytes = if ($uploadedMatch.Success) { [long]::Parse($uploadedMatch.Groups['bytes'].Value, [Globalization.CultureInfo]::InvariantCulture) } else { 0L }
            $speedBytesPerSecond = if ($speedMatch.Success) { [long]::Parse($speedMatch.Groups['bytesPerSecond'].Value, [Globalization.CultureInfo]::InvariantCulture) } else { 0L }
            if ($statusCode -eq 0) { throw "Gitee attachment upload transport failed: curl_exit=$exitCode http_status=000 seconds=$transferSeconds uploaded_bytes=$uploadedBytes speed_bytes_per_second=$speedBytesPerSecond." }
            $content = if (Test-Path -LiteralPath $responsePath -PathType Leaf) { [System.IO.File]::ReadAllText($responsePath) } else { '' }
            return [pscustomobject]@{ StatusCode = $statusCode; Content = $content; Headers = @{}; ExitCode = $exitCode; TimeSeconds = $transferSeconds; UploadedBytes = $uploadedBytes; SpeedBytesPerSecond = $speedBytesPerSecond }
        } finally {
            Remove-Item -LiteralPath $responsePath, $headersPath -Force -ErrorAction SilentlyContinue
        }
    }

    $requestTimer = [System.Diagnostics.Stopwatch]::StartNew()
    $request = [System.Net.HttpWebRequest]::Create($Uri)
    $request.Method = $Method
    $request.Timeout = [Math]::Min([int]::MaxValue, $TimeoutSec * 1000)
    $request.ReadWriteTimeout = [Math]::Min([int]::MaxValue, $TimeoutSec * 1000)
    $request.AllowAutoRedirect = $true
    $request.MaximumAutomaticRedirections = 10
    foreach ($headerName in $Headers.Keys) {
        switch ([string]$headerName) {
            { $_ -ieq 'User-Agent' } { $request.UserAgent = [string]$Headers[$headerName]; continue }
            { $_ -ieq 'Accept' } { $request.Accept = [string]$Headers[$headerName]; continue }
            default { $request.Headers[[string]$headerName] = [string]$Headers[$headerName] }
        }
    }
    if ($Method -in @('Post', 'Patch')) {
        $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes([string]$Body)
        $request.ContentType = $ContentType
        $request.ContentLength = $bodyBytes.Length
        $requestStream = $request.GetRequestStream()
        try { $requestStream.Write($bodyBytes, 0, $bodyBytes.Length) }
        finally { $requestStream.Dispose() }
    }

    $httpResponse = $null
    try {
        try { $httpResponse = [System.Net.HttpWebResponse]$request.GetResponse() }
        catch [System.Net.WebException] {
            if ($null -eq $_.Exception.Response) { throw }
            $httpResponse = [System.Net.HttpWebResponse]$_.Exception.Response
        }
        $statusCode = [int]$httpResponse.StatusCode
        $content = ''
        $responseStream = $httpResponse.GetResponseStream()
        if ($null -ne $responseStream) {
            if ($OutFile -and $statusCode -ge 200 -and $statusCode -lt 300) {
                $fileStream = [System.IO.File]::Open($OutFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
                try { $responseStream.CopyTo($fileStream) }
                finally { $fileStream.Dispose() }
            } else {
                $reader = [System.IO.StreamReader]::new($responseStream, [System.Text.Encoding]::UTF8, $true)
                try { $content = $reader.ReadToEnd() }
                finally { $reader.Dispose() }
            }
        }
        $requestTimer.Stop()
        return [pscustomobject]@{ StatusCode = $statusCode; Content = $content; Headers = $httpResponse.Headers; TimeSeconds = [math]::Round($requestTimer.Elapsed.TotalSeconds, 3) }
    } catch {
        $requestTimer.Stop()
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        $detail = "$($failure.GetType().Name): $($failure.Message)"
        $detail = Protect-GiteeDiagnostic $detail
        throw "HTTPS request to $(([uri]$Uri).Host) failed ($Method) after $([math]::Round($requestTimer.Elapsed.TotalSeconds, 3)) seconds: $detail"
    } finally {
        if ($null -ne $httpResponse) { $httpResponse.Dispose() }
    }
}

function Get-MirrorSha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-GitHubAssetSha256([object]$Asset) {
    $digest = [string]$Asset.digest
    if ($digest -notmatch '^sha256:(?<hash>[a-fA-F0-9]{64})$') {
        throw "GitHub Release API did not provide a SHA-256 digest for $($Asset.name)."
    }
    return $Matches.hash.ToLowerInvariant()
}

function Assert-GitHubAssetFile([object]$Asset, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Formal GitHub Release asset is missing locally: $($Asset.name)." }
    $file = Get-Item -LiteralPath $Path
    if ([long]$file.Length -ne [long]$Asset.size) { throw "GitHub asset size mismatch for $($Asset.name)." }
    $expectedHash = Get-GitHubAssetSha256 $Asset
    $actualHash = Get-MirrorSha256 $Path
    if ($actualHash -cne $expectedHash) { throw "GitHub asset SHA-256 mismatch for $($Asset.name)." }
    return $actualHash
}

function Get-MirrorJson([string]$Content, [string]$Context) {
    if ([string]::IsNullOrWhiteSpace($Content)) { throw "$Context returned an empty response." }
    try {
        if ($Content.TrimStart().StartsWith('[')) {
            $arrayEnvelope = ConvertFrom-Json -InputObject ('{"mirrorArray":' + $Content + '}') -ErrorAction Stop
            return ,$arrayEnvelope.mirrorArray
        }
        $parsed = ConvertFrom-Json -InputObject $Content -ErrorAction Stop
        return $parsed
    }
    catch { throw "$Context returned invalid JSON." }
}

function Get-GitHubMirrorApiHeaders {
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' }
    $token = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { $env:GITHUB_TOKEN } else { $env:GH_TOKEN }
    if (-not [string]::IsNullOrWhiteSpace($token) -and $token -notmatch '[\r\n]') {
        $headers.Authorization = "Bearer $token"
    }
    return $headers
}

function Get-GitHubReleaseForMirror([string]$Tag, [string]$Repository) {
    $uri = "https://api.github.com/repos/$Repository/releases/tags/$([uri]::EscapeDataString($Tag))"
    $response = Invoke-MirrorHttp -Uri $uri -Headers (Get-GitHubMirrorApiHeaders)
    if ($response.StatusCode -ne 200) { throw "GitHub Release lookup returned HTTP $($response.StatusCode) for $Tag. Provide a read-only GitHub API token or retry after the API limit resets." }
    $release = Get-MirrorJson $response.Content 'GitHub Release lookup'
    if ($release.tag_name -cne $Tag -or [bool]$release.draft -or [bool]$release.prerelease -ne $script:MirrorIsCandidate) {
        throw "GitHub Release $Tag has an unexpected publication/candidate state."
    }
    return $release
}

function Get-MirrorExpectedAssets([object]$Release, [string]$Tag, [string]$GitHubRepository, [string]$GiteeOwner, [string]$GiteeRepository, [byte[]]$ManifestBytes) {
    $version = (Get-NetBootReleaseIdentity -Tag $Tag -ReleaseCandidate:$script:MirrorIsCandidate).Version
    $manifestAsset = @($Release.assets | Where-Object { $_.name -ceq 'latest-v2.json' })
    $signatureAsset = @($Release.assets | Where-Object { $_.name -ceq 'latest-v2.json.sig' })
    if ($manifestAsset.Count -ne 1 -or $signatureAsset.Count -ne 1) { throw "GitHub Release $Tag must contain one latest-v2.json and one latest-v2.json.sig." }
    if ($null -eq $ManifestBytes -or $ManifestBytes.Length -eq 0) { throw 'GitHub latest-v2.json bytes were not verified before deriving the release asset set.' }
    $manifestText = [System.Text.UTF8Encoding]::new($false, $true).GetString($ManifestBytes)
    $manifest = Get-MirrorJson $manifestText 'GitHub latest-v2.json'
    if ([string]$manifest.version -cne $version -or
        [string]$manifest.archiveName -cne "NetBootDhcpTool-v$version.7z" -or
        [string]$manifest.archiveSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'GitHub latest-v2.json does not identify the requested tag and portable archive.'
    }
    $giteeBase = "https://gitee.com/$GiteeOwner/$GiteeRepository/releases/download/$Tag"
    $githubBase = "https://github.com/$GitHubRepository/releases/download/$Tag"
    $expectedArchiveUrl = "$giteeBase/$($manifest.archiveName)"
    $expectedArchiveMirror = "$githubBase/$($manifest.archiveName)"
    if ([string]$manifest.downloadUrl -cne $expectedArchiveUrl -or
        @($manifest.downloadMirrors | Where-Object { [string]$_ -ceq $expectedArchiveMirror }).Count -ne 1) {
        throw 'Signed archive URLs do not identify the matching current Gitee and GitHub Release assets.'
    }
    $packageEntries = @(
        @($manifest.packages) | ForEach-Object { [pscustomobject]@{ Package=$_; Format='Zip' } }
        @($manifest.sevenZipPackages) | ForEach-Object { [pscustomobject]@{ Package=$_; Format='SevenZip' } }
    )
    $fullSevenZip = @($packageEntries | Where-Object { $_.Format -ceq 'SevenZip' -and $_.Package.kind -ceq 'Full' })
    if ($fullSevenZip.Count -ne 1 -or $packageEntries.Count -gt 16) {
        throw 'GitHub latest-v2.json must declare exactly one Full 7z package and no more than sixteen packages.'
    }

    $names = [System.Collections.Generic.List[string]]::new()
    $names.Add([string]$manifest.archiveName)
    $names.Add(([string]$manifest.archiveName + '.sha256'))
    foreach ($entry in $packageEntries) {
        $package = $entry.Package
        $extension = if ($entry.Format -ceq 'SevenZip') { '7z' } else { 'zip' }
        if ([string]$package.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$package.size -le 0 -or
            [string]$package.fileName -notmatch "^NetBootDhcpTool-(full-v\d+\.\d+\.\d+|ota-v\d+\.\d+\.\d+-to-v\d+\.\d+\.\d+)\.$extension$") {
            throw 'GitHub latest-v2.json contains invalid update package metadata.'
        }
        if ([string]$package.kind -ceq 'Full') {
            if ([string]$package.fileName -cne "NetBootDhcpTool-full-v$version.$extension") { throw 'Full package filename does not match the requested version.' }
        } elseif ([string]$package.kind -ceq 'Ota') {
            if ([string]$package.fileName -notmatch "^NetBootDhcpTool-ota-v\d+\.\d+\.\d+-to-v$([regex]::Escape($version))\.$extension$") {
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
    $names.Add("NetBootDhcpTool-Setup-v$version.exe")
    $names.Add('latest-v2.json.sig')
    $names.Add('latest-v2.json')
    $names.Add('latest.json.sig')
    $names.Add('latest.json')
    if ($names.Count -ne @($names | Select-Object -Unique).Count) { throw 'GitHub latest-v2.json declares duplicate asset filenames.' }

    $releaseAssets = @($Release.assets)
    $duplicateNames = @($releaseAssets | Group-Object -Property name | Where-Object Count -gt 1)
    if ($duplicateNames.Count -gt 0) { throw 'GitHub Release contains duplicate asset filenames.' }
    foreach ($asset in $releaseAssets) { $null = Get-GitHubAssetSha256 $asset }
    foreach ($name in $names) {
        $matches = @($releaseAssets | Where-Object { $_.name -ceq $name })
        if ($matches.Count -ne 1) {
            throw "GitHub Release is missing required asset $name."
        }
        if ([string]$matches[0].browser_download_url -cne "$githubBase/$name") {
            throw "GitHub Release download URL is not canonical for $name."
        }
    }
    $unexpected = @($releaseAssets | Where-Object { $_.name -cnotin $names })
    if ($unexpected.Count -gt 0) { throw "GitHub Release contains an undeclared asset: $($unexpected[0].name)." }
    return [pscustomobject]@{ Manifest = $manifest; ManifestBytes = $ManifestBytes; AssetNames = @($names); Assets = $releaseAssets }
}

function Get-MirrorTrustedPublicKeyPem {
    $sourcePath = Join-Path $script:RepositoryRoot 'src\NetBootDhcpTool.Core\UpdatePackages.cs'
    $source = [System.IO.File]::ReadAllText($sourcePath)
    $match = [regex]::Match($source, '(?ms)public const string TrustedPublicKeyPem = """\r?\n(?<pem>.*?)\r?\n""";')
    if (-not $match.Success) { throw 'Could not read the update manifest public key from the client source.' }
    return $match.Groups['pem'].Value
}

function Read-MirrorDerElement([byte[]]$Bytes, [int]$Offset) {
    if ($Offset -lt 0 -or $Offset + 2 -gt $Bytes.Length) { throw 'Public-key DER data is truncated.' }
    $tag = [int]$Bytes[$Offset]
    $position = $Offset + 1
    $lengthByte = [int]$Bytes[$position]
    $position++
    if ($lengthByte -lt 128) {
        $length = $lengthByte
    } else {
        $lengthOctets = $lengthByte -band 127
        if ($lengthOctets -lt 1 -or $lengthOctets -gt 4 -or $position + $lengthOctets -gt $Bytes.Length) { throw 'Public-key DER length encoding is invalid.' }
        $length = 0
        for ($index = 0; $index -lt $lengthOctets; $index++) { $length = ($length -shl 8) -bor [int]$Bytes[$position + $index] }
        $position += $lengthOctets
    }
    if ($length -lt 0 -or $position + $length -gt $Bytes.Length) { throw 'Public-key DER element exceeds its buffer.' }
    return [pscustomobject]@{ Tag = $tag; ValueOffset = $position; Length = $length; EndOffset = $position + $length }
}

function Import-MirrorRsaPublicKey([System.Security.Cryptography.RSA]$Rsa, [string]$Pem) {
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $Rsa.ImportFromPem($Pem)
        return
    }

    $match = [regex]::Match($Pem, '(?s)-----BEGIN PUBLIC KEY-----\s*(?<base64>[A-Za-z0-9+/=\s]+)\s*-----END PUBLIC KEY-----')
    if (-not $match.Success) { throw 'Trusted update key is not a SubjectPublicKeyInfo PEM.' }
    $der = [Convert]::FromBase64String(($match.Groups['base64'].Value -replace '\s', ''))
    $sequence = Read-MirrorDerElement $der 0
    if ($sequence.Tag -ne 0x30) { throw 'Trusted update key DER has no outer sequence.' }
    $algorithm = Read-MirrorDerElement $der $sequence.ValueOffset
    if ($algorithm.Tag -ne 0x30) { throw 'Trusted update key DER has no algorithm identifier.' }
    $bitString = Read-MirrorDerElement $der $algorithm.EndOffset
    if ($bitString.Tag -ne 0x03 -or $bitString.Length -lt 1 -or $der[$bitString.ValueOffset] -ne 0) { throw 'Trusted update key DER has an invalid public-key bit string.' }
    $rsaSequence = Read-MirrorDerElement $der ($bitString.ValueOffset + 1)
    if ($rsaSequence.Tag -ne 0x30) { throw 'Trusted update key DER does not contain an RSA key.' }
    $modulusElement = Read-MirrorDerElement $der $rsaSequence.ValueOffset
    $exponentElement = Read-MirrorDerElement $der $modulusElement.EndOffset
    if ($modulusElement.Tag -ne 0x02 -or $exponentElement.Tag -ne 0x02 -or $modulusElement.Length -lt 1 -or $exponentElement.Length -lt 1) {
        throw 'Trusted update RSA key has invalid integer parameters.'
    }
    $modulus = [byte[]]$der[$modulusElement.ValueOffset..($modulusElement.EndOffset - 1)]
    $exponent = [byte[]]$der[$exponentElement.ValueOffset..($exponentElement.EndOffset - 1)]
    while ($modulus.Length -gt 1 -and $modulus[0] -eq 0) { $modulus = [byte[]]$modulus[1..($modulus.Length - 1)] }
    while ($exponent.Length -gt 1 -and $exponent[0] -eq 0) { $exponent = [byte[]]$exponent[1..($exponent.Length - 1)] }
    $parameters = New-Object System.Security.Cryptography.RSAParameters
    $parameters.Modulus = $modulus
    $parameters.Exponent = $exponent
    $Rsa.ImportParameters($parameters)
}

function Assert-MirrorManifestSignature([byte[]]$ManifestBytes, [string]$SignatureText) {
    try { $signature = [Convert]::FromBase64String($SignatureText.Trim()) }
    catch { throw 'GitHub latest.json.sig is not valid Base64.' }
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        Import-MirrorRsaPublicKey $rsa (Get-MirrorTrustedPublicKeyPem)
        if (-not $rsa.VerifyData($ManifestBytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)) {
            throw 'GitHub latest.json signature does not match the update key trusted by the client.'
        }
    } finally { $rsa.Dispose() }
}

function Get-GitHubTagCommitForMirror([string]$Tag, [string]$Repository) {
    $headers = Get-GitHubMirrorApiHeaders
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

function Get-GiteeAttachmentsForMirror([long]$ReleaseId, [string]$Owner, [string]$Repository, [string]$Token, [string]$Tag) {
    $items = [System.Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 10; $page++) {
        $uri = "https://gitee.com/api/v5/repos/$Owner/$Repository/releases/$ReleaseId/attach_files?page=$page&per_page=100"
        $response = Invoke-MirrorHttp -Uri $uri -Headers (Get-GiteeApiHeaders $Token)
        if ($response.StatusCode -ne 200) { throw "Gitee attachment listing returned HTTP $($response.StatusCode); refusing to upload without proving absence." }
        $pageItems = Get-MirrorJson $response.Content 'Gitee attachment listing'
        if ($pageItems -isnot [System.Collections.IList]) { throw 'Gitee attachment listing did not return an array.' }
        foreach ($item in $pageItems) {
            if ([string]$item.name -cin @("$Tag.zip", "$Tag.tar.gz")) { continue }
            $items.Add($item)
        }
        if ($pageItems.Count -lt 100) { return @($items) }
    }
    throw 'Gitee attachment listing exceeded the pagination limit; attachment absence is unknown.'
}

function Get-GiteeAttachmentById([long]$ReleaseId, [long]$AttachmentId, [string]$Owner, [string]$Repository, [string]$Token) {
    if ($AttachmentId -le 0) { throw 'Gitee attachment ID must be positive.' }
    $uri = "https://gitee.com/api/v5/repos/$Owner/$Repository/releases/$ReleaseId/attach_files/$AttachmentId"
    $response = Invoke-MirrorHttp -Uri $uri -Headers (Get-GiteeApiHeaders $Token)
    if ($response.StatusCode -ne 200) { throw "Gitee attachment lookup for ID $AttachmentId returned HTTP $($response.StatusCode)." }
    $attachment = Get-MirrorJson $response.Content "Gitee attachment ID $AttachmentId lookup"
    if ([string]$attachment.id -cne [string]$AttachmentId) { throw "Gitee attachment lookup returned an unexpected ID for $AttachmentId." }
    return $attachment
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

function Assert-GiteePublicAsset([object]$Attachment, [object]$LocalAsset, [long]$ReleaseId, [string]$Owner, [string]$Repository, [string]$Tag, [string]$TemporaryRoot, [string]$Stage) {
    if ([string]$Attachment.name -cne [string]$LocalAsset.Name) {
        throw "Gitee $Stage attachment filename mismatch for $($LocalAsset.Name)."
    }
    if ($Attachment.PSObject.Properties.Name -contains 'size' -and [long]$Attachment.size -ne [long]$LocalAsset.Size) {
        throw "Gitee $Stage attachment metadata size mismatch for $($LocalAsset.Name)."
    }
    $path = Join-Path $TemporaryRoot ("$Stage-" + [guid]::NewGuid().ToString('N') + '-' + [string]$LocalAsset.Name)
    $startedAt = [DateTimeOffset]::UtcNow
    try {
        $url = Get-GiteeMirrorDownloadUrl $Attachment $ReleaseId $Owner $Repository $Tag ([string]$LocalAsset.Name)
        $response = Invoke-MirrorHttp -Uri $url -Headers @{ 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' } -OutFile $path -TimeoutSec 180
        $endedAt = [DateTimeOffset]::UtcNow
        if ($response.StatusCode -ne 200 -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Gitee $Stage public download failed for $($LocalAsset.Name) (HTTP $($response.StatusCode))."
        }
        $file = Get-Item -LiteralPath $path
        $hash = Get-MirrorSha256 $path
        if ([long]$file.Length -ne [long]$LocalAsset.Size -or $hash -cne [string]$LocalAsset.Sha256) {
            throw "Gitee $Stage public download size/SHA-256 mismatch for $($LocalAsset.Name)."
        }
        return [pscustomobject]@{
            Filename = [string]$Attachment.name
            Size = [long]$file.Length
            Sha256 = $hash
            HttpStatus = [int]$response.StatusCode
            StartedAtUtc = $startedAt.ToString('o')
            EndedAtUtc = $endedAt.ToString('o')
            DownloadSeconds = [math]::Round(($endedAt - $startedAt).TotalSeconds, 3)
            AttachmentId = [long]$Attachment.id
        }
    } finally {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

function Write-GiteeMirrorTelemetry([string]$Path, [string]$Tag, [long]$ReleaseId, [string]$State, [string]$CurrentAsset, [System.Collections.IDictionary]$Timings) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $tempPath = $fullPath + '.partial-' + [guid]::NewGuid().ToString('N')
    $assets = @($Timings.Values | ForEach-Object { $_ })
    $report = [ordered]@{
        schemaVersion = 1
        tag = $Tag
        releaseId = $ReleaseId
        state = $State
        currentAsset = $CurrentAsset
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        assets = $assets
    }
    try {
        $json = ConvertTo-Json -InputObject $report -Depth 8
        Assert-GiteeCredentialFreeText $json
        [System.IO.File]::WriteAllText($tempPath, $json, [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $tempPath -Destination $fullPath -Force
    } finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}

function Read-VerifiedMirrorSidecar([string]$Path, [string]$Name, [string]$ExpectedHash) {
    $parts = ([System.IO.File]::ReadAllText($Path)).Trim() -split '\s+', 2
    if ($parts.Count -ne 2 -or $parts[0] -cne $ExpectedHash -or $parts[1] -cne $Name) {
        throw "SHA-256 sidecar content is invalid for $Name."
    }
}

function Download-VerifiedGitHubAsset([object]$Asset, [string]$Destination) {
    $downloadUrl = $null
    if (-not [uri]::TryCreate([string]$Asset.browser_download_url, [UriKind]::Absolute, [ref]$downloadUrl) -or
        $downloadUrl.Scheme -cne 'https' -or $downloadUrl.Host -cne 'github.com' -or
        [long]$Asset.size -le 0) {
        throw "GitHub asset metadata is invalid for $($Asset.name)."
    }
    $partialPath = $Destination + '.partial-' + [guid]::NewGuid().ToString('N')
    try {
        $response = Invoke-MirrorHttp -Uri $downloadUrl.AbsoluteUri -Headers @{ 'User-Agent' = 'NetBootDhcpTool-GiteeMirror/1.0' } -OutFile $partialPath -TimeoutSec 180
        if ($response.StatusCode -ne 200) { throw "GitHub download failed for $($Asset.name) with HTTP $($response.StatusCode)." }
        $null = Assert-GitHubAssetFile $Asset $partialPath
        Move-Item -LiteralPath $partialPath -Destination $Destination -Force
    } finally {
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
    }
}

function New-GiteeReleaseForMirror([string]$Tag, [string]$Owner, [string]$Repository, [string]$Token, [string]$TargetCommit, [object]$Manifest) {
    $existing = Get-GiteeReleaseForMirror $Tag $Owner $Repository $Token
    if ($null -ne $existing) {
        if ($script:MirrorIsCandidate -and -not [bool]$existing.prerelease) { throw 'An existing stable Gitee Release cannot be reused as an RC.' }
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

function Resolve-MirrorSourceAsset([object]$Asset, [string]$SourceDirectory, [bool]$AllowDownload) {
    $path = Join-Path $SourceDirectory ([string]$Asset.name)
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $hash = Assert-GitHubAssetFile $Asset $path
        Write-Host "[GitHub] Reusing verified local asset $($Asset.name) ($($Asset.size) bytes, $hash)."
    } elseif ($AllowDownload) {
        Download-VerifiedGitHubAsset $Asset $path
        $hash = Assert-GitHubAssetFile $Asset $path
        Write-Host "[GitHub] Downloaded and verified $($Asset.name) ($($Asset.size) bytes, $hash)."
    } else {
        throw "AssetDirectory is missing formal GitHub Release asset $($Asset.name)."
    }
    $file = Get-Item -LiteralPath $path
    return [pscustomobject]@{ Name = [string]$Asset.name; Path = $path; Sha256 = $hash; Size = [long]$file.Length }
}

function Publish-GiteeMirror {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tag,
        [string]$Token = $env:GITEE_TOKEN,
        [string]$AssetDirectory,
        [ValidateRange(30, 300)][int]$UploadTimeoutSec = 180,
        [string]$TelemetryPath,
        [string]$GitHubRepository = 'shashouaq/NetBootDhcpTool',
        [string]$GiteeOwner = 'joel20230302',
        [string]$GiteeRepository = 'NetBootDhcpTool'
    )

    if (-not [string]::IsNullOrWhiteSpace($Token) -and $Token -match '[\r\n]') { throw 'GITEE_TOKEN must not contain a line break.' }
    foreach ($repoPart in @($GitHubRepository, "$GiteeOwner/$GiteeRepository")) {
        if ($repoPart -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository must be in owner/repository form.' }
    }

    $version = (Get-NetBootReleaseIdentity -Tag $Tag -ReleaseCandidate:$script:MirrorIsCandidate).Version
    $release = Get-GitHubReleaseForMirror $Tag $GitHubRepository
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('netboot-gitee-mirror-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    try {
        $useLocalAssets = -not [string]::IsNullOrWhiteSpace($AssetDirectory)
        if ($useLocalAssets) {
            $sourceRoot = [System.IO.Path]::GetFullPath($AssetDirectory)
            if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "AssetDirectory does not exist: $sourceRoot" }
            $allowDownload = $false
            Write-Host "[Assets] Using existing official Release files in $sourceRoot."
        } else {
            $sourceRoot = Join-Path $script:MirrorRoot ("artifacts\gitee-mirror-assets\$Tag")
            New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
            $allowDownload = $true
            Write-Host "[Assets] GitHub downloads will be retained in $sourceRoot for safe local resumption."
        }

        $manifestAsset = @($release.assets | Where-Object { $_.name -ceq 'latest-v2.json' })
        if ($manifestAsset.Count -ne 1) { throw "GitHub Release $Tag must contain exactly one latest-v2.json." }
        $manifestRecord = Resolve-MirrorSourceAsset $manifestAsset[0] $sourceRoot $allowDownload
        $manifestBytes = [System.IO.File]::ReadAllBytes($manifestRecord.Path)
        $expected = Get-MirrorExpectedAssets $release $Tag $GitHubRepository $GiteeOwner $GiteeRepository $manifestBytes

        if ($useLocalAssets) {
            $expectedNames = @($expected.AssetNames)
            $actualNames = @(Get-ChildItem -LiteralPath $sourceRoot -File | ForEach-Object { [string]$_.Name })
            $unexpectedNames = @($actualNames | Where-Object { $_ -cnotin $expectedNames })
            $missingNames = @($expectedNames | Where-Object { $_ -cnotin $actualNames })
            if ($unexpectedNames.Count -gt 0 -or $missingNames.Count -gt 0 -or $actualNames.Count -ne $expectedNames.Count) {
                $unexpectedSummary = if ($unexpectedNames.Count -gt 0) { $unexpectedNames -join ', ' } else { '(none)' }
                $missingSummary = if ($missingNames.Count -gt 0) { $missingNames -join ', ' } else { '(none)' }
                throw "AssetDirectory must contain exactly the formal GitHub Release assets; unexpected=[$unexpectedSummary], missing=[$missingSummary]."
            }
        }

        $localAssets = [ordered]@{}
        foreach ($name in $expected.AssetNames) {
            $asset = @($expected.Assets | Where-Object { $_.name -ceq $name })[0]
            if ($name -ceq 'latest-v2.json') {
                $record = $manifestRecord
            } else {
                $record = Resolve-MirrorSourceAsset $asset $sourceRoot $allowDownload
            }
            $localAssets[$name] = $record
        }

        $manifest = $expected.Manifest
        $manifestPath = $localAssets['latest-v2.json'].Path
        $signatureText = [System.IO.File]::ReadAllText($localAssets['latest-v2.json.sig'].Path)
        Assert-MirrorManifestSignature -ManifestBytes ([System.IO.File]::ReadAllBytes($manifestPath)) -SignatureText $signatureText
        if ([string]$manifest.version -cne $version) { throw 'Signed V2 manifest version does not match the requested tag.' }

        $legacyDirectory = Join-Path $script:RepositoryRoot 'build\legacy\v1.0.20'
        foreach ($legacyName in @('latest.json', 'latest.json.sig')) {
            $legacySource = Join-Path $legacyDirectory $legacyName
            if (-not (Test-Path -LiteralPath $legacySource -PathType Leaf) -or (Get-FileHash -LiteralPath $legacySource -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $localAssets[$legacyName].Path -Algorithm SHA256).Hash) {
                throw "GitHub Release changed the byte-preserved v1.0.20 legacy asset: $legacyName."
            }
        }
        $setupAssetName = "NetBootDhcpTool-Setup-v$version.exe"

        $archiveName = [string]$manifest.archiveName
        $archiveHash = $localAssets[$archiveName].Sha256
        if ($archiveHash -cne ([string]$manifest.archiveSha256).ToLowerInvariant()) { throw 'Portable archive SHA-256 does not match signed latest.json.' }
        Read-VerifiedMirrorSidecar $localAssets[($archiveName + '.sha256')].Path $archiveName $archiveHash
        $packageEntries = @(
            @($manifest.packages) | ForEach-Object { [pscustomobject]@{ Package=$_; Format='Zip' } }
            @($manifest.sevenZipPackages) | ForEach-Object { [pscustomobject]@{ Package=$_; Format='SevenZip' } }
        )
        foreach ($entry in $packageEntries) {
            $package = $entry.Package
            $packageName = [string]$package.fileName
            $record = $localAssets[$packageName]
            if ($null -eq $record -or $record.Sha256 -cne ([string]$package.sha256).ToLowerInvariant() -or
                $record.Size -ne [long]$package.size) { throw "Update package does not match signed metadata: $packageName." }
            Read-VerifiedMirrorSidecar $localAssets[($packageName + '.sha256')].Path $packageName $record.Sha256
        }

        Assert-GiteeAssetSetSizePreflight -Assets $localAssets

        if ([string]::IsNullOrWhiteSpace($Token)) {
            throw "GITEE_TOKEN is not configured. Verified official assets are retained in $sourceRoot; set GITEE_TOKEN and rerun with -AssetDirectory `"$sourceRoot`"."
        }
        $targetCommit = Get-GitHubTagCommitForMirror $Tag $GitHubRepository
        $giteeRelease = New-GiteeReleaseForMirror $Tag $GiteeOwner $GiteeRepository $Token $targetCommit $manifest
        $releaseId = [long]$giteeRelease.id
        if ($releaseId -le 0) { throw "Gitee Release ID is invalid for $Tag." }
        $headers = Get-GiteeApiHeaders $Token

        $preflightAttachments = Get-GiteeAttachmentsForMirror $releaseId $GiteeOwner $GiteeRepository $Token $Tag
        foreach ($attachment in $preflightAttachments) {
            if ([string]$attachment.name -cnotin $expected.AssetNames) { throw "Gitee Release contains an undeclared attachment: $($attachment.name)." }
        }
        $preflightVerified = @{}
        foreach ($name in $expected.AssetNames) {
            $matches = @($preflightAttachments | Where-Object { [string]$_.name -ceq $name })
            if ($matches.Count -gt 1) { throw "Gitee Release has duplicate attachments named $name; refusing to delete or overwrite any of them." }
            if ($matches.Count -eq 1) {
                $local = $localAssets[$name]
                $verificationStartedAt = [DateTimeOffset]::UtcNow
                $attachment = Get-GiteeAttachmentById $releaseId ([long]$matches[0].id) $GiteeOwner $GiteeRepository $Token
                if ([string]$attachment.name -cne $name) { throw "Gitee attachment ID $($matches[0].id) resolved to a different filename during preflight." }
                $verification = Assert-GiteePublicAsset $attachment $local $releaseId $GiteeOwner $GiteeRepository $Tag $temporaryRoot 'preflight'
                $verification | Add-Member -NotePropertyName StartedAtUtc -NotePropertyValue $verificationStartedAt.ToString('o') -Force
                $preflightVerified[$name] = $verification
                Write-Host "[Gitee] Preflight verified existing $name; attachment_id=$($attachment.id) http=$($verification.HttpStatus) size=$($verification.Size) sha256=$($verification.Sha256)."
            }
        }

        $payloadNames = @(
            $expected.AssetNames |
                Where-Object { $_ -notin @('latest.json', 'latest.json.sig', 'latest-v2.json', 'latest-v2.json.sig') } |
                ForEach-Object { [pscustomobject]@{ Name = [string]$_; Size = [long]$localAssets[[string]$_].Size } } |
                Sort-Object -Property Size, Name |
                ForEach-Object { $_.Name }
        )
        $orderedNames = $payloadNames + @('latest.json.sig', 'latest.json', 'latest-v2.json.sig', 'latest-v2.json')
        $uploadTimings = [ordered]@{}
        Write-GiteeMirrorTelemetry $TelemetryPath $Tag $releaseId 'RUNNING' '' $uploadTimings
        foreach ($name in $orderedNames) {
            $local = $localAssets[$name]
            $attachments = Get-GiteeAttachmentsForMirror $releaseId $GiteeOwner $GiteeRepository $Token $Tag
            $matches = @($attachments | Where-Object { [string]$_.name -ceq $name })
            if ($matches.Count -gt 1) { throw "Gitee Release has duplicate attachments named $name; refusing to delete or overwrite any of them." }
            if ($matches.Count -eq 1) {
                $attachmentId = [long]$matches[0].id
                if (-not $preflightVerified.ContainsKey($name)) {
                    $reusedAttachment = Get-GiteeAttachmentById $releaseId $attachmentId $GiteeOwner $GiteeRepository $Token
                    $preflightVerified[$name] = Assert-GiteePublicAsset $reusedAttachment $local $releaseId $GiteeOwner $GiteeRepository $Tag $temporaryRoot 'reuse'
                }
                $verification = $preflightVerified[$name]
                $timing = [pscustomobject]@{
                    State = 'REUSED'; Filename = $name; Size = [long]$local.Size; StartedAtUtc = [string]$verification.StartedAtUtc
                    EndedAtUtc = [string]$verification.EndedAtUtc; Seconds = 0.0; AverageBytesPerSecond = 0.0
                    HttpStatus = [int]$verification.HttpStatus; AttachmentId = $attachmentId; Sha256 = [string]$local.Sha256; NetworkError = ''
                }
                $uploadTimings[$name] = $timing
                Write-GiteeMirrorTelemetry $TelemetryPath $Tag $releaseId 'RUNNING' $name $uploadTimings
                Write-Host "[Gitee] Asset transfer: file=$name size=$($local.Size) state=REUSED start_utc=$($timing.StartedAtUtc) end_utc=$($timing.EndedAtUtc) seconds=0 avg_bytes_per_sec=0 http=$($timing.HttpStatus) attachment_id=$attachmentId network_error=none."
                continue
            }

            $startedAt = [DateTimeOffset]::UtcNow
            $uploadStatus = 0
            $attachmentId = 0L
            $networkError = ''
            $upload = $null
            Write-Host "[Gitee] Upload starting: file=$name size=$($local.Size) start_utc=$($startedAt.ToString('o'))."
            $uploadUri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/$releaseId/attach_files"
            $uploadTimer = [System.Diagnostics.Stopwatch]::StartNew()
            $uploadEndedAt = $null
            try {
                $upload = Invoke-MirrorHttp -Uri $uploadUri -Method Post -Headers $headers -UploadFile $local.Path -FormFields @{ access_token = $Token } -TimeoutSec $UploadTimeoutSec
                $uploadTimer.Stop()
                $uploadEndedAt = [DateTimeOffset]::UtcNow
                $elapsed = if ($null -ne $upload.TimeSeconds) { [double]$upload.TimeSeconds } else { [math]::Round($uploadTimer.Elapsed.TotalSeconds, 3) }
                $uploadStatus = [int]$upload.StatusCode
                if ($upload.StatusCode -notin @(200, 201)) {
                    $recovered = @(Get-GiteeAttachmentsForMirror $releaseId $GiteeOwner $GiteeRepository $Token $Tag | Where-Object { [string]$_.name -ceq $name })
                    if ($recovered.Count -ne 1) {
                        throw "Gitee upload for $name returned HTTP $($upload.StatusCode) (curl exit $($upload.ExitCode)); no unique attachment was recovered."
                    }
                    $attachmentId = [long]$recovered[0].id
                    $state = 'RECOVERED'
                } else {
                    $created = Get-MirrorJson $upload.Content "Gitee upload for $name"
                    if ([string]$created.name -cne $name -or [long]$created.id -le 0) { throw "Gitee upload returned an invalid attachment identity for $name." }
                    $attachmentId = [long]$created.id
                    $state = 'UPLOADED'
                }
                $attachment = Get-GiteeAttachmentById $releaseId $attachmentId $GiteeOwner $GiteeRepository $Token
                if ([string]$attachment.name -cne $name) { throw "Gitee attachment ID $attachmentId resolved to a different filename after upload." }
                $verification = Assert-GiteePublicAsset $attachment $local $releaseId $GiteeOwner $GiteeRepository $Tag $temporaryRoot 'immediate'
                $endedAt = [DateTimeOffset]::UtcNow
                $elapsed = if ($null -ne $upload.TimeSeconds) { [double]$upload.TimeSeconds } else { [math]::Round($uploadTimer.Elapsed.TotalSeconds, 3) }
                $averageBytesPerSecond = if ($elapsed -gt 0) { [math]::Round([double]$local.Size / $elapsed, 2) } else { 0.0 }
                $timing = [pscustomobject]@{
                    State = $state; Filename = $name; Size = [long]$local.Size; StartedAtUtc = $startedAt.ToString('o')
                    EndedAtUtc = $uploadEndedAt.ToString('o'); Seconds = [math]::Round($elapsed, 3); AverageBytesPerSecond = $averageBytesPerSecond
                    HttpStatus = $uploadStatus; AttachmentId = $attachmentId; Sha256 = [string]$local.Sha256; NetworkError = ''
                    CurlExitCode = $upload.ExitCode; CurlUploadedBytes = $upload.UploadedBytes; CurlSpeedBytesPerSecond = $upload.SpeedBytesPerSecond
                    PublicDownloadHttpStatus = $verification.HttpStatus; PublicDownloadSeconds = $verification.DownloadSeconds
                }
                $uploadTimings[$name] = $timing
                Write-GiteeMirrorTelemetry $TelemetryPath $Tag $releaseId 'RUNNING' $name $uploadTimings
                Write-Host "[Gitee] Asset transfer: file=$name size=$($local.Size) state=$state start_utc=$($timing.StartedAtUtc) end_utc=$($timing.EndedAtUtc) seconds=$($timing.Seconds) avg_bytes_per_sec=$averageBytesPerSecond http=$uploadStatus attachment_id=$attachmentId curl_uploaded_bytes=$($upload.UploadedBytes) curl_speed_bytes_per_sec=$($upload.SpeedBytesPerSecond) public_http=$($verification.HttpStatus) public_sha256=$($verification.Sha256) network_error=none."
            } catch {
                $uploadTimer.Stop()
                $endedAt = [DateTimeOffset]::UtcNow
                if ($null -eq $uploadEndedAt) { $uploadEndedAt = $endedAt }
                $elapsed = [math]::Round($uploadTimer.Elapsed.TotalSeconds, 3)
                $networkError = Protect-GiteeDiagnostic ([string]$_.Exception.Message) @($Token)
                $averageBytesPerSecond = if ($elapsed -gt 0) { [math]::Round([double]$local.Size / $elapsed, 2) } else { 0.0 }
                $curlExit = if ($null -ne $upload) { $upload.ExitCode } else { $null }
                $curlUploaded = if ($null -ne $upload) { $upload.UploadedBytes } else { $null }
                $curlSpeed = if ($null -ne $upload) { $upload.SpeedBytesPerSecond } else { $null }
                if ($null -eq $upload) {
                    $curlExitMatch = [regex]::Match($networkError, 'curl_exit=(?<value>\d+)')
                    $uploadedMatch = [regex]::Match($networkError, 'uploaded_bytes=(?<value>\d+)')
                    $speedMatch = [regex]::Match($networkError, 'speed_bytes_per_second=(?<value>\d+)')
                    if ($curlExitMatch.Success) { $curlExit = [int]$curlExitMatch.Groups['value'].Value }
                    if ($uploadedMatch.Success) { $curlUploaded = [long]$uploadedMatch.Groups['value'].Value }
                    if ($speedMatch.Success) { $curlSpeed = [long]$speedMatch.Groups['value'].Value }
                    $statusMatch = [regex]::Match($networkError, 'http_status=(?<value>\d{3})')
                    if ($statusMatch.Success) { $uploadStatus = [int]$statusMatch.Groups['value'].Value }
                }
                $uploadTimings[$name] = [pscustomobject]@{
                    State = 'FAILED'; Filename = $name; Size = [long]$local.Size; StartedAtUtc = $startedAt.ToString('o')
                    EndedAtUtc = $uploadEndedAt.ToString('o'); Seconds = $elapsed; AverageBytesPerSecond = $averageBytesPerSecond
                    HttpStatus = $uploadStatus; AttachmentId = $attachmentId; Sha256 = [string]$local.Sha256; NetworkError = $networkError
                    CurlExitCode = $curlExit; CurlUploadedBytes = $curlUploaded; CurlSpeedBytesPerSecond = $curlSpeed
                }
                Write-GiteeMirrorTelemetry $TelemetryPath $Tag $releaseId 'FAILED' $name $uploadTimings
                Write-Host "[Gitee] Asset transfer: file=$name size=$($local.Size) state=FAILED start_utc=$($startedAt.ToString('o')) end_utc=$($uploadEndedAt.ToString('o')) seconds=$elapsed avg_bytes_per_sec=$averageBytesPerSecond http=$uploadStatus attachment_id=$attachmentId curl_exit=$curlExit curl_uploaded_bytes=$curlUploaded curl_speed_bytes_per_sec=$curlSpeed network_error=$networkError."
                throw
            }
        }

        $allAttachments = Get-GiteeAttachmentsForMirror $releaseId $GiteeOwner $GiteeRepository $Token $Tag
        foreach ($attachment in $allAttachments) {
            if ([string]$attachment.name -cnotin $expected.AssetNames) { throw "Gitee Release contains an undeclared attachment: $($attachment.name)." }
        }
        foreach ($name in $expected.AssetNames) {
            $matches = @($allAttachments | Where-Object { [string]$_.name -ceq $name })
            if ($matches.Count -ne 1) { throw "Gitee final inventory has $($matches.Count) attachments named $name; expected exactly one." }
            $local = $localAssets[$name]
            $attachment = Get-GiteeAttachmentById $releaseId ([long]$matches[0].id) $GiteeOwner $GiteeRepository $Token
            $verification = Assert-GiteePublicAsset $attachment $local $releaseId $GiteeOwner $GiteeRepository $Tag $temporaryRoot 'final'
            Write-Host "[Gitee] Final public download verified: file=$name size=$($verification.Size) sha256=$($verification.Sha256) http=$($verification.HttpStatus) attachment_id=$($verification.AttachmentId)."
        }

        if (-not $script:MirrorIsCandidate -and [bool]$giteeRelease.prerelease) {
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

        $confirmedRelease = Get-GiteeReleaseForMirror $Tag $GiteeOwner $GiteeRepository $Token
        if ($null -eq $confirmedRelease -or [bool]$confirmedRelease.prerelease -ne $script:MirrorIsCandidate) { throw 'Gitee Release has an unexpected final candidate/stable state after verification.' }
        if (-not $script:MirrorIsCandidate) {
            $latestUri = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepository/releases/latest"
            $latestResponse = Invoke-MirrorHttp -Uri $latestUri -Headers $headers
            if ($latestResponse.StatusCode -ne 200) { throw "Gitee latest Release lookup returned HTTP $($latestResponse.StatusCode)." }
            $latestRelease = Get-MirrorJson $latestResponse.Content 'Gitee latest Release'
            if ([string]$latestRelease.tag_name -cne $Tag) { throw "Gitee latest Release is $($latestRelease.tag_name), expected $Tag." }
        }

        foreach ($name in $orderedNames) {
            if (-not $uploadTimings.Contains($name)) {
                $verification = $preflightVerified[$name]
                $uploadTimings[$name] = [pscustomobject]@{
                    State = 'REUSED'; Filename = $name; Size = [long]$localAssets[$name].Size; StartedAtUtc = [string]$verification.StartedAtUtc
                    EndedAtUtc = [string]$verification.EndedAtUtc; Seconds = 0.0; AverageBytesPerSecond = 0.0
                    HttpStatus = [int]$verification.HttpStatus; AttachmentId = [long]$verification.AttachmentId; Sha256 = [string]$localAssets[$name].Sha256; NetworkError = ''
                }
            }
            $timing = $uploadTimings[$name]
            Write-Host "[Gitee] Asset transfer summary: file=$name size=$($timing.Size) state=$($timing.State) start_utc=$($timing.StartedAtUtc) end_utc=$($timing.EndedAtUtc) seconds=$($timing.Seconds) avg_bytes_per_sec=$($timing.AverageBytesPerSecond) http=$($timing.HttpStatus) attachment_id=$($timing.AttachmentId) sha256=$($timing.Sha256) network_error=$($timing.NetworkError)."
        }

        Write-GiteeMirrorTelemetry $TelemetryPath $Tag $releaseId 'SUCCESS' '' $uploadTimings
        $success = "GITEE_MIRROR SUCCESS tag=$Tag release_id=$releaseId assets=$($expected.AssetNames.Count) archive_sha256=$archiveHash"
        Write-Host $success
        return [pscustomobject]@{
            Succeeded = $true
            Tag = $Tag
            AssetCount = $expected.AssetNames.Count
            ArchiveSha256 = $archiveHash
            ReleaseId = $releaseId
            UploadTimings = $uploadTimings
            AssetDirectory = $sourceRoot
        }
    } finally {
        $tempFull = [System.IO.Path]::GetFullPath($temporaryRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        if ([System.IO.Path]::GetDirectoryName($tempFull) -ceq $tempBase -and [System.IO.Path]::GetFileName($tempFull).StartsWith('netboot-gitee-mirror-', [System.StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $tempFull -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if (-not $LibraryOnly) {
    try {
        Invoke-WithGiteeCredential -CredentialPath $CredentialPath -Operation {
            $null = Publish-GiteeMirror -Tag $Tag -AssetDirectory $AssetDirectory -UploadTimeoutSec $UploadTimeoutSec -TelemetryPath $TelemetryPath -GitHubRepository $GitHubRepository -GiteeOwner $GiteeOwner -GiteeRepository $GiteeRepository
        }
    } catch {
        $message = Protect-GiteeDiagnostic ([string]$_.Exception.Message)
        $message = $message -replace '[\r\n]+', ' '
        Write-Host "GITEE_MIRROR FAILED tag=$Tag reason=$message" -ForegroundColor Red
        throw $message
    }
}
