$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $repo 'build/release-pipeline/ReleaseTransport.psm1') -Force
$module=Get-Module ReleaseTransport
& $module {
    $script:caseCount=0
    function script:Start-Sleep {param($Seconds);$script:delays+=$Seconds}
    function script:Invoke-WebRequest {
        param($Uri,$Method,$Headers,$Body,$ContentType,$OutFile,$PassThru,$InFile,$SkipHttpErrorCheck,$ConnectionTimeoutSeconds,$OperationTimeoutSeconds,$MaximumRedirection,$ErrorAction)
        if($ConnectionTimeoutSeconds -ne 15 -or $OperationTimeoutSeconds -ne 30){throw 'timeout propagation failed'}
        $script:lastRedirectLimit=$MaximumRedirection
        $script:calls++
        $step=$script:steps[[math]::Min($script:calls-1,$script:steps.Count-1)]
        if($step -is [string]){throw $step}
        return @{StatusCode=$step;Content='{}';Headers=@{'Retry-After'=$(if($step -eq 429){'7'}else{''})}}
    }
    function script:Invoke-ReleasePythonHttp {
        param($Uri,$Headers,$OutFile,$ConnectTimeoutSec,$ReadTimeoutSec)
        $script:fallbackCalls++
        if($script:fallbackFailure){throw $script:fallbackFailure}
        if($OutFile){[IO.File]::WriteAllText($OutFile,'verified bytes')}
        return @{StatusCode=200;Content='{}';Headers=@{}}
    }
    function script:Check-Http {
        param($Steps,$Attempts,$Delays,[string]$Failure='',[string]$Uri='https://gitee.com/api/test')
        $script:steps=@($Steps);$script:calls=0;$script:delays=@();$script:fallbackCalls=0;$script:fallbackFailure=''
        try{$response=Invoke-ReleaseHttp -Uri $Uri -Stage fault-test;if($Failure){throw 'expected failure missing'};if($response.StatusCode -ne $Steps[-1]){throw 'wrong final status'}}
        catch{if(-not $Failure -or $_.Exception.Message -notmatch $Failure){throw}}
        if($script:calls -ne $Attempts -or $script:delays.Count -ne $Delays){throw "unexpected retry count: $script:calls/$($script:delays.Count)"}
        $script:caseCount++
    }
    Check-Http @('unexpected EOF',200) 2 1
    Check-Http @(500,502,599,200) 4 3
    Check-Http @(429,200) 2 1
    if($script:delays[0] -ne 7){throw 'Retry-After ignored'}
    Check-Http @('connect timeout',200) 2 1
    Check-Http @('read timeout',200) 2 1
    Check-Http @('connection reset',200) 2 1
    Check-Http @('handshake interrupted',200) 2 1
    foreach($status in @(401,403,404)){Check-Http @($status) 1 0}
    Check-Http @('certificate validation failure unexpected EOF') 1 0 'category=certificate'
    Check-Http @('unexpected EOF') 4 3 'after 4 attempt'
    $script:steps=@('unexpected EOF');$script:calls=0;$script:delays=@();$script:fallbackCalls=0;$script:fallbackFailure=''
    $response=Invoke-ReleaseHttp -Uri 'https://api.github.com/repos/test/repo' -Headers @{Authorization='Bearer test-secret'}
    if($script:calls -ne 1 -or $script:fallbackCalls -ne 1 -or $response.StatusCode -ne 200){throw 'GitHub API fallback failed'};$script:caseCount++
    $script:steps=@('unexpected EOF');$script:calls=0;$script:delays=@();$script:fallbackCalls=0;$script:fallbackFailure='certificate validation failure'
    try{$null=Invoke-ReleaseHttp -Uri 'https://api.github.com/repos/test/repo';throw 'expected failure missing'}catch{if($_.Exception.Message -notmatch 'category=certificate'){throw}}
    if($script:calls -ne 1 -or $script:fallbackCalls -ne 1){throw 'fallback certificate was retried'};$script:caseCount++
    foreach($method in @('Post','Patch','Delete')){
        $script:steps=@('connection reset');$script:calls=0;$script:delays=@();$script:fallbackCalls=0
        try{$null=Invoke-ReleaseHttp -Uri 'https://gitee.com/api/test' -Method $method;throw 'expected failure missing'}catch{if($_.Exception.Message -notmatch 'after 1 attempt'){throw}}
        if($script:calls -ne 1 -or $script:delays.Count){throw 'write was blindly retried'};$script:caseCount++
    }
    foreach($method in @('Post','Patch','Delete')){
        foreach($status in @(307,308)){
            $script:steps=@($status);$script:calls=0;$script:delays=@();$script:fallbackCalls=0
            $response=Invoke-ReleaseHttp -Uri 'https://gitee.com/api/test' -Method $method -MaximumRedirection 10
            if($script:lastRedirectLimit -ne 0 -or $script:calls -ne 1 -or $script:delays.Count -or $response.StatusCode -ne $status){throw 'Write request may follow a redirect and repeat its side effect'}
            $script:caseCount++
        }
    }
    function script:Get-Command {return @{Source='Invoke-FaultCurl'}}
    function script:Invoke-FaultCurl {
        $script:calls++
        $path=$args[[array]::IndexOf($args,'--output')+1]
        $header=$args[[array]::IndexOf($args,'--dump-header')+1]
        [IO.File]::WriteAllText($header,'')
        if($script:downloadMode -eq 'partial' -and $script:calls -eq 1){[IO.File]::WriteAllText($path,'partial');$global:LASTEXITCODE=18;return 'http=200 unexpected EOF'}
        if($script:downloadMode -eq 'eof'){[IO.File]::WriteAllText($path,'partial');$global:LASTEXITCODE=35;return 'http=000 unexpected EOF'}
        if($script:downloadMode -eq 'certificate'){$global:LASTEXITCODE=60;return 'http=000 certificate validation failure'}
        [IO.File]::WriteAllText($path,'verified bytes');$global:LASTEXITCODE=0;return 'http=200'
    }
    $file=Join-Path $env:TEMP ('netboot-http-fault-'+[guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($file,'verified bytes');$hash=(Get-FileHash $file).Hash
    try{
        $script:calls=0;$script:delays=@();$script:downloadMode='partial'
        $null=Invoke-ReleaseDownload -Uri 'https://gitee.com/download/test' -Destination $file -AssetName test -ExpectedSize 14 -ExpectedSha256 $hash
        if($script:calls -ne 2 -or [IO.File]::ReadAllText($file) -cne 'verified bytes'){throw 'partial retry failed'};$script:caseCount++
        $script:calls=0;$script:downloadMode='eof';$script:fallbackCalls=0;$script:fallbackFailure=''
        $null=Invoke-ReleaseDownload -Uri 'https://github.com/test/repo/releases/download/v1/test' -Destination $file -AssetName test -ExpectedSize 14 -ExpectedSha256 $hash
        if($script:calls -ne 1 -or $script:fallbackCalls -ne 1){throw 'GitHub asset fallback failed'};$script:caseCount++
        $script:calls=0;$script:downloadMode='success'
        try{$null=Invoke-ReleaseDownload -Uri 'https://gitee.com/download/test' -Destination $file -AssetName test -ExpectedSize 14 -ExpectedSha256 ('f'*64);throw 'expected failure missing'}catch{if($_.Exception.Message -notmatch 'SHA-256 mismatch'){throw}}
        if($script:calls -ne 1 -or [IO.File]::ReadAllText($file) -cne 'verified bytes'){throw 'hash mismatch altered destination or retried'};$script:caseCount++
        try{$null=Invoke-ReleaseDownload -Uri 'https://gitee.com/download/test' -Destination $file -AssetName test -ExpectedSize 15 -ExpectedSha256 $hash;throw 'expected failure missing'}catch{if($_.Exception.Message -notmatch 'size mismatch'){throw}};$script:caseCount++
        $script:calls=0;$script:downloadMode='certificate';$script:fallbackCalls=0
        try{$null=Invoke-ReleaseDownload -Uri 'https://github.com/test/repo/releases/download/v1/test' -Destination $file -AssetName test -ExpectedSize 14 -ExpectedSha256 $hash;throw 'expected failure missing'}catch{if($_.Exception.Message -notmatch 'category=certificate'){throw}}
        if($script:calls -ne 1 -or $script:fallbackCalls){throw 'curl certificate bypassed'};$script:caseCount++
        if(@(Get-ChildItem -LiteralPath (Split-Path $file) -Filter ((Split-Path -Leaf $file)+'.partial-*')).Count){throw 'unverified partial leaked'}
    }finally{Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue}
    Write-Output "HTTP_RESILIENCE_TESTS_OK cases=$script:caseCount"
}
Import-Module (Join-Path $repo 'build/release-pipeline/ReleaseTransport.psm1') -Force

# Exercise the current GitHub publisher's uncertain upload path, not a parallel implementation.
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/Publish-GitHubRelease.ps1'),[ref]$null,[ref]$null)
$functions=$ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)
& {
    . ([scriptblock]::Create(($functions.Extent.Text -join "`n")))
    $script:writes=0;$localAssets=@{test=@{Path='fixture';Size=14;Sha256='a'*64}};$GitHubRepository='test/repo'
    function Get-AssetByName {param($Release,$Name);if($script:writes){return @{name='test';size=14}};return $null}
    function Invoke-GitHubApi {$script:writes++;throw 'unexpected EOF'}
    function Get-GitHubRelease {return @{id=1}}
    function Test-GitHubAssetReadback {param($Asset,$Name);if($Asset.size -ne 14){throw 'invalid recovered identity'}}
    $null=Ensure-GitHubAsset @{id=1} test
    if($script:writes -ne 1){throw 'uncertain stored upload repeated'}
    Write-Output 'HTTP_UPLOAD_RECONCILIATION_OK cases=1'
}

# The archived publisher still shares the same read layer and preserves its old
# gh-shaped metadata contract. No CLI/network write is executed in this test.
$legacyAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'build/publish-release.ps1'),[ref]$null,[ref]$null)
$legacyFunction=$legacyAst.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-GitHubRelease'},$false)[0]
& {
    . ([scriptblock]::Create($legacyFunction.Extent.Text))
    $Tag='v1.1.1';$GitHubRepository='test/repo';$script:status=200;$script:reads=0
    function Invoke-ReleaseHttp {param($Uri,$Headers,$Stage);$script:reads++;return @{StatusCode=$script:status;Content='{"tag_name":"v1.1.1","name":"fixture","body":"fixture","draft":false,"prerelease":true,"assets":[{"name":"test","url":"api-url","browser_download_url":"public-url","size":14}]}'} }
    $result=Get-GitHubRelease
    if($result.tagName -cne $Tag -or $result.isDraft -or -not $result.isPrerelease -or $result.assets[0].apiUrl -cne 'api-url'){throw 'Archived metadata contract changed'}
    $script:status=404;if($null -ne (Get-GitHubRelease)){throw 'Expected absence was not returned'}
    $script:status=401
    try{$null=Get-GitHubRelease;throw 'expected failure missing'}catch{if($_.Exception.Message -notmatch 'HTTP 401'){throw}}
    if($script:reads -ne 3){throw 'Archived lookup did not use the shared transport exactly once per call'}
    Write-Output 'HTTP_LEGACY_READ_CONTRACT_OK cases=3'
}
