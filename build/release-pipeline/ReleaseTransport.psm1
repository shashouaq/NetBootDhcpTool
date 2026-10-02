Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ReleaseRetryDelay {
    param([int]$Attempt,[string]$RetryAfter='', [switch]$Jitter)
    $seconds=0;$date=[DateTimeOffset]::MinValue
    if([int]::TryParse($RetryAfter,[ref]$seconds)){return [math]::Max(0,$seconds)}
    if([DateTimeOffset]::TryParse($RetryAfter,[ref]$date)){return [int][math]::Max(0,[math]::Ceiling(($date-[DateTimeOffset]::UtcNow).TotalSeconds))}
    $delay=[int][math]::Min(20,[math]::Pow(2,$Attempt))
    if($Jitter){$delay+=Get-Random -Minimum 0 -Maximum 3};return $delay
}
function Test-ReleaseTransientStatus {
    param([int]$StatusCode)
    return $StatusCode -in @(408,429) -or ($StatusCode -ge 500 -and $StatusCode -le 599)
}
function Get-ReleaseTransportCategory {
    param([object]$Failure)
    $errorObject=if($Failure -is [Management.Automation.ErrorRecord]){$Failure.Exception}else{$Failure}
    $detail=''
    while($null -ne $errorObject){$detail+=' '+[string]$errorObject;if($errorObject -isnot [Exception]){break};$errorObject=$errorObject.InnerException}
    if($detail -match '(?i)certificate|cert_verify|trustfailure|untrusted|revocation'){return 'certificate'}
    if($detail -match '(?i)unexpected.*EOF|unexpected end|0 bytes from the transport|response ended prematurely|\[eof\]'){return 'eof'}
    if($detail -match '(?i)connect.*timeout|connection.*timed out|\[connect-timeout\]'){return 'connect-timeout'}
    if($detail -match '(?i)read.*timeout|read.*timed out|\[read-timeout\]'){return 'read-timeout'}
    if($detail -match '(?i)timeout|timed out|TaskCanceledException|OperationCanceledException'){return 'timeout'}
    if($detail -match '(?i)connection.*reset|forcibly closed|wsarecv|broken pipe|\[connection-reset\]'){return 'connection-reset'}
    if($detail -match '(?i)handshake.*interrupted|\[tls-interruption\]'){return 'tls-interruption'}
    if($detail -match '(?i)NameResolutionFailure|HostNotFound|ConnectionRefused|could not resolve|cannot connect|failed to connect'){return 'connect'}
    return 'permanent-transport'
}
function Invoke-ReliableReleaseOperation {
    param([Parameter(Mandatory)][scriptblock]$Operation,[ValidateSet('Get','Post','Patch','Delete')][string]$Method='Get',
        [ValidateRange(1,6)][int]$MaximumAttempts=4,[ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Stage='release-http')
    # Writes have ambiguous outcomes and must be reconciled by the owning publisher.
    if($Method -ne 'Get'){$MaximumAttempts=1};$timer=[Diagnostics.Stopwatch]::StartNew()
    for($attempt=1;$attempt -le $MaximumAttempts;$attempt++){
        $response=$null
        try{$response=& $Operation}catch{
            $category=Get-ReleaseTransportCategory $_
            if($category -in @('certificate','permanent-transport') -or $attempt -eq $MaximumAttempts){throw "Release HTTP $Method transport failed after $attempt attempt(s); category=$category stage=$Stage."}
            $delay=Get-ReleaseRetryDelay -Attempt $attempt -Jitter
        }
        if($null -ne $response){
            $status=[int]$response.StatusCode
            if(-not(Test-ReleaseTransientStatus $status) -or $attempt -eq $MaximumAttempts){return $response}
            $category="http-$status";$retryAfter=''
            if($response.Headers -and $response.Headers['Retry-After']){$retryAfter=[string](@($response.Headers['Retry-After'])[0])}
            $delay=Get-ReleaseRetryDelay -Attempt $attempt -RetryAfter $retryAfter -Jitter
        }
        if($delay -gt 60){throw "Release HTTP $Method Retry-After exceeds this run's retry budget; stage=$Stage."}
        Write-Host "[Retry] attempt=$attempt/$MaximumAttempts stage=$Stage category=$category elapsed=$([math]::Round($timer.Elapsed.TotalSeconds,3))s delay=$($delay)s"
        Start-Sleep -Seconds $delay
    }
}
function Invoke-ReleaseNativeHttp {
    param($Uri,$Method,$Headers,$Body,$ContentType,$OutFile,$ConnectTimeoutSec,$ReadTimeoutSec,$MaximumRedirection)
    $request=[Net.HttpWebRequest]::Create($Uri);$request.Method=$Method
    $request.Timeout=$ConnectTimeoutSec*1000;$request.ReadWriteTimeout=$ReadTimeoutSec*1000
    # Follow GET redirects explicitly on Windows PowerShell too, enforcing HTTPS
    # and dropping API credentials before any change of host.
    $request.AllowAutoRedirect=$false
    foreach($name in $Headers.Keys){switch($name){'User-Agent'{$request.UserAgent=$Headers[$name]}'Accept'{$request.Accept=$Headers[$name]}default{$request.Headers[$name]=$Headers[$name]}}}
    if($Method -in @('Post','Patch')){
        $bytes=[Text.Encoding]::UTF8.GetBytes($Body);$request.ContentType=$ContentType;$request.ContentLength=$bytes.Length
        $stream=$request.GetRequestStream();try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    }
    $response=$null
    try{
        try{$response=$request.GetResponse()}catch [Net.WebException]{if(-not $_.Exception.Response){throw};$response=$_.Exception.Response}
        if ($Method -eq 'Get' -and [int]$response.StatusCode -in @(301,302,303,307,308) -and $MaximumRedirection -gt 0) {
            $next = [uri]::new([uri]$Uri,[string]$response.Headers['Location'])
            if ($next.Scheme -cne 'https' -or $next.UserInfo) { throw 'Insecure redirect rejected.' }
            $nextHeaders=$Headers.Clone()
            if ($next.Host -ine ([uri]$Uri).Host) { $nextHeaders.Remove('Authorization') }
            $response.Dispose();$response=$null
            return Invoke-ReleaseNativeHttp -Uri $next.AbsoluteUri -Method Get -Headers $nextHeaders -Body '' -ContentType $ContentType -OutFile $OutFile -ConnectTimeoutSec $ConnectTimeoutSec -ReadTimeoutSec $ReadTimeoutSec -MaximumRedirection ($MaximumRedirection-1)
        }
        $content='';$stream=$response.GetResponseStream()
        if($OutFile -and [int]$response.StatusCode -eq 200){
            $file=[IO.File]::Open($OutFile,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{$stream.CopyTo($file)}finally{$file.Dispose()}
        }else{$reader=[IO.StreamReader]::new($stream);try{$content=$reader.ReadToEnd()}finally{$reader.Dispose()}}
        return [pscustomobject]@{StatusCode=[int]$response.StatusCode;Content=$content;Headers=$response.Headers}
    }finally{if($response){$response.Dispose()}}
}
function Invoke-ReleasePythonHttp {
    param([string]$Uri,[hashtable]$Headers=@{},[string]$OutFile,[int]$ConnectTimeoutSec=15,[int]$ReadTimeoutSec=60)
    $python=if($env:NETBOOT_HTTPX_PYTHON){$env:NETBOOT_HTTPX_PYTHON}else{(Get-Command python.exe -ErrorAction Stop).Source}
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$python;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.Arguments='"'+(Join-Path $PSScriptRoot 'reliable_http.py')+'"'
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    if($env:NETBOOT_HTTPX_PYTHONPATH){$start.EnvironmentVariables['PYTHONPATH']=$env:NETBOOT_HTTPX_PYTHONPATH}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try{
        $null=$process.Start()
        $inputJson=ConvertTo-Json -Compress -Depth 5 -InputObject @{uri=$Uri;headers=$Headers;outFile=$OutFile;connectTimeout=$ConnectTimeoutSec;readTimeout=$ReadTimeoutSec}
        # Secrets only travel over stdin, never arguments, URLs or diagnostic output.
        $process.StandardInput.WriteLine($inputJson);$process.StandardInput.Close();$inputJson=$null
        $outputTask=$process.StandardOutput.ReadToEndAsync();$errorTask=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(240000)){$process.Kill();throw '[timeout] Python HTTP exceeded its bounded deadline.'}
        $result=$outputTask.GetAwaiter().GetResult() | ConvertFrom-Json;$null=$errorTask.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0 -or -not $result.ok){throw "[$($result.category)] Python HTTPS request failed."}
        $responseHeaders=@{};foreach($property in $result.headers.PSObject.Properties){$responseHeaders[$property.Name]=$property.Value}
        return [pscustomobject]@{StatusCode=[int]$result.status;Content=[string]$result.content;Headers=$responseHeaders}
    }finally{$process.Dispose()}
}
function Invoke-ReleaseHttp {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Uri,[ValidateSet('Get','Post','Patch','Delete')][string]$Method='Get',
        [hashtable]$Headers=@{},[string]$Body,[string]$ContentType='application/json; charset=utf-8',
        [ValidateRange(1,6)][int]$MaximumAttempts=4,[ValidateRange(1,300)][int]$TimeoutSec=30,
        [ValidateRange(1,300)][int]$ConnectTimeoutSec=15,[string]$OutFile,[string]$InFile,
        [ValidateRange(0,10)][int]$MaximumRedirection=10,[string]$Stage='release-http')
    $parsed=[uri]$Uri
    if($parsed.Scheme -cne 'https' -or $parsed.UserInfo -or $parsed.Query -match '(?i)token|authorization|secret'){throw 'Release HTTP requires HTTPS without URL credentials.'}
    # A 307/308 redirect can repeat a write even when this wrapper attempts it
    # only once. Let the owning publisher reconcile the original endpoint.
    if ($Method -ne 'Get') { $MaximumRedirection = 0 }
    if ($Headers.ContainsKey('Authorization') -and -not (
        $parsed.Host -in @('api.github.com','uploads.github.com') -or
        ($parsed.Host -eq 'gitee.com' -and $parsed.AbsolutePath.StartsWith('/api/')))) {
        throw 'Authorization is restricted to release API hosts and paths.'
    }
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $response=Invoke-ReliableReleaseOperation -Method $Method -MaximumAttempts $MaximumAttempts -Stage $Stage -Operation {
        try{
            if($PSVersionTable.PSVersion -lt [version]'7.4'){
                if($InFile){throw 'Binary GitHub uploads require PowerShell 7.4 or later.'}
                Invoke-ReleaseNativeHttp -Uri $Uri -Method $Method -Headers $Headers -Body $Body -ContentType $ContentType -OutFile $OutFile -ConnectTimeoutSec $ConnectTimeoutSec -ReadTimeoutSec $TimeoutSec -MaximumRedirection $MaximumRedirection
            }else{
                $parameters=@{Uri=$Uri;Method=$Method;Headers=$Headers;SkipHttpErrorCheck=$true;ConnectionTimeoutSeconds=$ConnectTimeoutSec;OperationTimeoutSeconds=$TimeoutSec;MaximumRedirection=$MaximumRedirection;ErrorAction='Stop'}
                if($Method -in @('Post','Patch')){$parameters.Body=$Body;$parameters.ContentType=$ContentType}
                if($OutFile){$parameters.OutFile=$OutFile;$parameters.PassThru=$true}
                if($InFile){$parameters.Remove('Body');$parameters.InFile=$InFile;$parameters.ContentType=$ContentType}
                Invoke-WebRequest @parameters
            }
        }catch{
            if($Method -eq 'Get' -and $parsed.Host -in @('api.github.com','github.com') -and (Get-ReleaseTransportCategory $_) -in @('eof','tls-interruption')){
                Write-Host "[Fallback] stage=$Stage category=eof transport=python-httpx"
                Invoke-ReleasePythonHttp -Uri $Uri -Headers $Headers -OutFile $OutFile -ConnectTimeoutSec $ConnectTimeoutSec -ReadTimeoutSec $TimeoutSec
            }else{throw}
        }
    }
    if($response){return [pscustomobject]@{StatusCode=[int]$response.StatusCode;Content=[string]$response.Content;Headers=$response.Headers;TimeSeconds=[math]::Round($timer.Elapsed.TotalSeconds,3)}}
}
function Invoke-ReleaseDownload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Uri,[Parameter(Mandatory)][string]$Destination,[Parameter(Mandatory)][string]$AssetName,
        [ValidateRange(1,6)][int]$MaximumAttempts=4,[long]$ExpectedSize=-1,[string]$ExpectedSha256,[string]$Stage='release-download')
    $parsed=[uri]$Uri;$trustedHost=$parsed.Host -in @('github.com','gitee.com') -or $parsed.Host.EndsWith('.gitee.com',[StringComparison]::OrdinalIgnoreCase)
    if($parsed.Scheme -cne 'https' -or -not $trustedHost -or $parsed.UserInfo -or $parsed.Query -or $parsed.Fragment){throw 'Release downloads must use a credential-free HTTPS GitHub/Gitee URL.'}
    if($ExpectedSha256 -and $ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Invalid expected SHA-256.'}
    $curl=(Get-Command curl.exe -ErrorAction Stop).Source
    $partial=$Destination+'.partial-'+[guid]::NewGuid().ToString('N');$headersPath=$partial+'.headers'
    try{
        $response=Invoke-ReliableReleaseOperation -MaximumAttempts $MaximumAttempts -Stage $Stage -Operation {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            $output=& $curl --disable --silent --show-error --location --proto '=https' --proto-redir '=https' --connect-timeout 15 --max-time 180 --speed-limit 1 --speed-time 60 --fail --dump-header $headersPath --output $partial --write-out 'http=%{http_code}' $Uri 2>&1 | Out-String
            $exitCode=$LASTEXITCODE;$match=[regex]::Match($output,'http=(\d{3})');$status=if($match.Success){[int]$match.Groups[1].Value}else{0}
            $responseHeaders=@{}
            if(Test-Path -LiteralPath $headersPath){$text=[IO.File]::ReadAllText($headersPath);$retryMatches=[regex]::Matches($text,'(?im)^Retry-After:\s*([^\r\n]+)');if($retryMatches.Count){$responseHeaders['Retry-After']=$retryMatches[$retryMatches.Count-1].Groups[1].Value}}
            if($exitCode -eq 0 -and $status -eq 200){return @{StatusCode=200;Headers=$responseHeaders}}
            if($exitCode -in @(51,60,77,83,90,91) -or (Get-ReleaseTransportCategory $output) -eq 'certificate'){throw '[certificate] curl certificate validation failed.'}
            if($status -ge 400){return @{StatusCode=$status;Headers=$responseHeaders}}
            $category=Get-ReleaseTransportCategory $output
            if($parsed.Host -eq 'github.com' -and ($category -eq 'eof' -or $exitCode -in @(18,52,56,92))){
                if ($ExpectedSize -lt 0 -or -not $ExpectedSha256) { throw '[eof] Asset fallback requires expected size and SHA-256 metadata.' }
                Write-Host "[Fallback] stage=$Stage category=eof transport=python-httpx"
                return Invoke-ReleasePythonHttp -Uri $Uri -OutFile $partial
            }
            if($exitCode -eq 28){throw '[timeout] curl request timed out.'}
            if($exitCode -in @(6,7)){throw 'failed to connect'}
            if($exitCode -in @(18,52,55,56,92)){throw '[connection-reset] curl transport interrupted.'}
            if($category -in @('eof','tls-interruption')){throw "[$category] curl transport interrupted."}
            throw 'Permanent curl transport failure.'
        }
        if($response.StatusCode -ne 200 -or -not(Test-Path -LiteralPath $partial -PathType Leaf)){throw "Download failed for $AssetName (HTTP $($response.StatusCode))."}
        if($ExpectedSize -ge 0 -and (Get-Item -LiteralPath $partial).Length -ne $ExpectedSize){throw "Download size mismatch for $AssetName."}
        if($ExpectedSha256 -and (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine $ExpectedSha256){throw "Download SHA-256 mismatch for $AssetName."}
        # No unverified partial becomes a final asset. Legacy callers use temporary readback paths.
        $fullDestination=[IO.Path]::GetFullPath($Destination)
        if([IO.File]::Exists($fullDestination)){[IO.File]::Replace($partial,$fullDestination,[Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($partial,$fullDestination)}
        return [pscustomobject]@{StatusCode=200;Content='';Headers=$response.Headers}
    }finally{Remove-Item -LiteralPath $partial,$headersPath -Force -ErrorAction SilentlyContinue}
}
Export-ModuleMember -Function Get-ReleaseRetryDelay,Test-ReleaseTransientStatus,Get-ReleaseTransportCategory,Invoke-ReliableReleaseOperation,Invoke-ReleaseHttp,Invoke-ReleaseDownload,Invoke-ReleasePythonHttp
