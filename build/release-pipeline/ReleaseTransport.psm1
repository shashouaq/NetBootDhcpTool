Set-StrictMode -Version Latest

function Get-ReleaseRetryDelay {
    param([int]$Attempt, [string]$RetryAfter = '')
    $seconds = 0
    $date = [DateTimeOffset]::MinValue
    if ([int]::TryParse($RetryAfter, [ref]$seconds)) { return [Math]::Max(0, $seconds) }
    if ([DateTimeOffset]::TryParse($RetryAfter, [ref]$date)) {
        return [int][Math]::Max(0, [Math]::Ceiling(($date - [DateTimeOffset]::UtcNow).TotalSeconds))
    }
    return [int][Math]::Min(20, [Math]::Pow(2, $Attempt))
}

function Test-ReleaseTransientStatus {
    param([int]$StatusCode)
    return $StatusCode -in @(0, 408, 429, 500, 502, 503, 504)
}

function Invoke-ReleaseHttp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [ValidateSet('Get', 'Post', 'Patch', 'Delete')][string]$Method = 'Get',
        [hashtable]$Headers = @{},
        [string]$Body,
        [string]$ContentType = 'application/json; charset=utf-8',
        [ValidateRange(1, 4)][int]$MaximumAttempts = 4,
        [ValidateRange(1, 60)][int]$TimeoutSec = 30
    )
    # POST has an ambiguous outcome when the connection is lost. Reconcile its
    # returned identity or tag before any further mutation; never blindly retry it.
    if ($Method -eq 'Post') { $MaximumAttempts = 1 }
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $response = $null
        $parameters = @{ Uri = $Uri; Method = $Method; Headers = $Headers; SkipHttpErrorCheck = $true; TimeoutSec = $TimeoutSec; ErrorAction = 'Stop' }
        if ($Method -in @('Post', 'Patch')) { $parameters.Body = $Body; $parameters.ContentType = $ContentType }
        try { $response = Invoke-WebRequest @parameters }
        catch {
            if ($attempt -eq $MaximumAttempts) { throw "Release HTTP $Method transport failed after $attempt attempt(s) at $(([uri]$Uri).Host)." }
        }
        $status = if ($null -eq $response) { 0 } else { [int]$response.StatusCode }
        if (-not (Test-ReleaseTransientStatus $status) -or $attempt -eq $MaximumAttempts) { return $response }
        $retryAfter = if ($null -ne $response) { [string](@($response.Headers['Retry-After'])[0]) } else { '' }
        $delay = Get-ReleaseRetryDelay -Attempt $attempt -RetryAfter $retryAfter
        if ($delay -gt 60) { throw "Release HTTP $Method returned HTTP $status with Retry-After beyond this run's retry budget." }
        Write-Host "[Retry] HTTP $Method $status from $(([uri]$Uri).Host); attempt $attempt/$MaximumAttempts, waiting $delay seconds."
        Start-Sleep -Seconds $delay
    }
}

function Invoke-ReleaseDownload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$AssetName,
        [ValidateRange(1, 4)][int]$MaximumAttempts = 4
    )
    $parsed = [uri]$Uri
    $trustedHost = $parsed.Host -in @('github.com', 'gitee.com') -or $parsed.Host.EndsWith('.gitee.com', [StringComparison]::OrdinalIgnoreCase)
    if ($parsed.Scheme -cne 'https' -or -not $trustedHost -or $parsed.UserInfo -or $parsed.Query) {
        throw 'Release downloads must use a credential-free HTTPS GitHub/Gitee URL.'
    }
    $curl = (Get-Command curl.exe -ErrorAction Stop).Source
    $headersPath = "$Destination.headers"
    try {
        for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
            # Overwrite only this attempt's temporary output, never an existing release asset.
            $output = & $curl --silent --show-error --location --proto '=https' --proto-redir '=https' --connect-timeout 15 --max-time 180 --fail --dump-header $headersPath --output $Destination --write-out 'http=%{http_code}' $Uri 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
            $statusMatch = [regex]::Match($output, 'http=(\d{3})')
            $status = if ($statusMatch.Success) { [int]$statusMatch.Groups[1].Value } else { 0 }
            if ($exitCode -eq 0 -and $status -eq 200 -and (Test-Path -LiteralPath $Destination -PathType Leaf)) { return }
            $transient = (Test-ReleaseTransientStatus $status) -or ($status -lt 400 -and $exitCode -in @(6, 7, 18, 28, 35, 52, 55, 56, 92))
            if (-not $transient -or $attempt -eq $MaximumAttempts) {
                throw "Download failed for $AssetName from $($parsed.Host) (HTTP $status, curl exit $exitCode, attempt $attempt)."
            }
            $retryAfter = ''
            if (Test-Path -LiteralPath $headersPath) {
                $headerText = Get-Content -LiteralPath $headersPath -Raw
                if (-not [string]::IsNullOrEmpty($headerText)) {
                    $headerMatches = @([regex]::Matches($headerText, '(?im)^Retry-After:\s*([^\r\n]+)'))
                    if ($headerMatches.Count -gt 0) { $retryAfter = $headerMatches[-1].Groups[1].Value }
                }
            }
            $delay = Get-ReleaseRetryDelay -Attempt $attempt -RetryAfter $retryAfter
            if ($delay -gt 60) { throw "Download rate limit for $AssetName exceeds this run's retry budget." }
            Write-Host "[Retry] Download $AssetName HTTP $status/curl $exitCode; waiting $delay seconds."
            Start-Sleep -Seconds $delay
        }
    } finally { Remove-Item -LiteralPath $headersPath -Force -ErrorAction SilentlyContinue }
}

Export-ModuleMember -Function Get-ReleaseRetryDelay, Test-ReleaseTransientStatus, Invoke-ReleaseHttp, Invoke-ReleaseDownload
