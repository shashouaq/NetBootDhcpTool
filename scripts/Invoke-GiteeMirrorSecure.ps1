#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [switch]$ReleaseCandidate,
    [switch]$SynchronizeSource,
    [string]$StateDirectory,
    [string]$CredentialPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repositoryRoot 'build\release-identity.ps1')
. (Join-Path $PSScriptRoot 'Get-GiteeCredential.ps1') -LoadOnly
$null = Get-NetBootReleaseIdentity -Tag $Tag -ReleaseCandidate:$ReleaseCandidate
if (-not (Test-Path -LiteralPath $AssetDirectory -PathType Container)) { throw 'Verified AssetDirectory is missing.' }
if (-not $StateDirectory) { $StateDirectory = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($AssetDirectory))) 'mirror-state' }
[IO.Directory]::CreateDirectory($StateDirectory) | Out-Null
$runId = [guid]::NewGuid().ToString('N')
$statusPath = Join-Path $StateDirectory ($Tag + '-' + $runId + '.status.json')
$telemetryPath = Join-Path $StateDirectory ($Tag + '-' + $runId + '.telemetry.json')
$publisherPath = Join-Path $PSScriptRoot 'Publish-GiteeMirror.ps1'
$state = 'INITIALIZING_CREDENTIAL'
$detail = ''
$secure = $null
$resultCode = 1

function Write-SecureMirrorStatus {
    $record = [ordered]@{ runId=$runId; tag=$Tag; state=$state; updatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
        credentialCleared=(-not [bool]$env:GITEE_TOKEN); detail=$detail }
    $temporary = $statusPath + '.partial'
    try {
        $json = ConvertTo-Json -InputObject $record -Compress
        Assert-GiteeCredentialFreeText $json
        [IO.File]::WriteAllText($temporary,$json,[Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $statusPath -Force
    } finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
}

function Sync-GiteeReleaseSource {
    # Source preparation is separate from the API-only asset publisher.
    . $publisherPath -Tag $Tag -ReleaseCandidate:$ReleaseCandidate -AssetDirectory $AssetDirectory -LibraryOnly
    $release = Get-GitHubReleaseForMirror $Tag 'shashouaq/NetBootDhcpTool'
    foreach ($asset in $release.assets) { $null = Assert-GitHubAssetFile $asset (Join-Path $AssetDirectory $asset.name) }
    Assert-MirrorManifestSignature ([IO.File]::ReadAllBytes((Join-Path $AssetDirectory 'latest-v2.json'))) ([IO.File]::ReadAllText((Join-Path $AssetDirectory 'latest-v2.json.sig')))
    $commit = Get-GitHubTagCommitForMirror $Tag 'shashouaq/NetBootDhcpTool'
    if ($ReleaseCandidate -and [string]$release.target_commitish -cne $commit) { throw 'GitHub RC tag moved away from its immutable build commit.' }
    $query = Invoke-MirrorHttp -Uri ('https://gitee.com/api/v5/repos/joel20230302/NetBootDhcpTool/commits/' + $commit) -Headers (Get-GiteeApiHeaders $env:GITEE_TOKEN)
    if ($query.StatusCode -eq 200) {
        if ([string](Get-MirrorJson $query.Content 'Gitee commit readback').sha -cne $commit) { throw 'Gitee source commit identity mismatch.' }
        Write-Host '[Gitee] Matching release source commit is already present.'
        return
    }
    if ($query.StatusCode -ne 404) { throw "Gitee source preflight returned HTTP $($query.StatusCode); no source synchronization attempted." }
    $names = @('GIT_CONFIG_COUNT','GIT_CONFIG_KEY_0','GIT_CONFIG_VALUE_0','GIT_CONFIG_KEY_1','GIT_CONFIG_VALUE_1',
        'GIT_TERMINAL_PROMPT','GCM_INTERACTIVE','GIT_TRACE','GIT_TRACE2','GIT_TRACE_CURL','GIT_CURL_VERBOSE')
    $saved = @{}; foreach ($name in $names) { $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process') }
    $credentialBytes=$null; $basic=$null
    try {
        $credentialBytes=[Text.Encoding]::UTF8.GetBytes('joel20230302:'+$env:GITEE_TOKEN)
        $basic=[Convert]::ToBase64String($credentialBytes)
        $env:GIT_CONFIG_COUNT='2'; $env:GIT_CONFIG_KEY_0='credential.helper'; $env:GIT_CONFIG_VALUE_0=''
        $env:GIT_CONFIG_KEY_1='http.https://gitee.com/.extraHeader'; $env:GIT_CONFIG_VALUE_1='Authorization: Basic '+$basic
        $env:GIT_TERMINAL_PROMPT='0'; $env:GCM_INTERACTIVE='Never'
        foreach ($name in @('GIT_TRACE','GIT_TRACE2','GIT_TRACE_CURL')) { [Environment]::SetEnvironmentVariable($name,'0','Process') }
        # Git treats GIT_CURL_VERBOSE as enabled when it exists, including value "0".
        Remove-Item Env:GIT_CURL_VERBOSE -ErrorAction SilentlyContinue
        Push-Location -LiteralPath $repositoryRoot
        try {
            $commands = @(
                @('fetch','https://github.com/shashouaq/NetBootDhcpTool.git',('refs/tags/'+$Tag+':refs/tags/'+$Tag)),
                @('fetch','https://gitee.com/joel20230302/NetBootDhcpTool.git','+refs/heads/main:refs/remotes/gitee/main'),
                @('merge-base','--is-ancestor','refs/remotes/gitee/main',$commit)
            )
            foreach ($arguments in $commands) {
                $output = & git @arguments 2>&1
                $code=$LASTEXITCODE
                $safeOutput=Protect-GiteeDiagnostic ($output | Out-String) @($basic)
                Assert-GiteeCredentialFreeText $safeOutput @($basic)
                if ($safeOutput.Trim()) { Write-Host $safeOutput.Trim() }
                if ($code -ne 0) { throw "Gitee source preparation failed at $($arguments[0]): git exit=$code. No force push or automatic credential replacement." }
            }
            $localCommit=(& git rev-parse ('refs/tags/'+$Tag+'^{commit}')).Trim()
            if ($LASTEXITCODE -ne 0 -or $localCommit -cne $commit) { throw 'Source tag does not match the signed GitHub candidate commit.' }
            $output = & git push --atomic 'https://gitee.com/joel20230302/NetBootDhcpTool.git' ($commit+':refs/heads/main') ('refs/tags/'+$Tag+':refs/tags/'+$Tag) 2>&1
            $code=$LASTEXITCODE
            $safeOutput=Protect-GiteeDiagnostic ($output | Out-String) @($basic)
            Assert-GiteeCredentialFreeText $safeOutput @($basic)
            if ($safeOutput.Trim()) { Write-Host $safeOutput.Trim() }
            if ($code -ne 0) { throw "Gitee source preparation failed at push: git exit=$code. No force push or automatic credential replacement." }
        } finally { Pop-Location }
    } finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$saved[$name],'Process') }
        if ($credentialBytes) { [Array]::Clear($credentialBytes,0,$credentialBytes.Length) }; $basic=$null
    }
    $verified = Invoke-MirrorHttp -Uri ('https://gitee.com/api/v5/repos/joel20230302/NetBootDhcpTool/commits/' + $commit) -Headers (Get-GiteeApiHeaders $env:GITEE_TOKEN)
    if ($verified.StatusCode -ne 200 -or [string](Get-MirrorJson $verified.Content 'Gitee source readback').sha -cne $commit) { throw 'Gitee source public/API readback failed.' }
    Write-Host '[Gitee] Matching release source synchronized without rewriting history.'
}

Write-SecureMirrorStatus
Write-Host ('Status: '+$statusPath)
Write-Host ('Telemetry: '+$telemetryPath)
try {
    $secure=Get-GiteeCredential -CredentialPath $CredentialPath -AllowPrompt
    $store=Resolve-GiteeCredentialPath $CredentialPath
    if (-not (Test-Path -LiteralPath $store)) { $null=Set-GiteeCredential -Credential $secure -CredentialPath $store }
    $check=Test-GiteeCredential -Credential $secure
    if (-not $check.Success) { throw $check.Reason }
    $secure.Dispose(); $secure=$null
    $state='RUNNING'; Write-SecureMirrorStatus
    Invoke-WithGiteeCredential -CredentialPath $CredentialPath -Operation {
        if ($SynchronizeSource) { Sync-GiteeReleaseSource }
        & $publisherPath -Tag $Tag -AssetDirectory $AssetDirectory -TelemetryPath $telemetryPath -CredentialPath $CredentialPath -ReleaseCandidate:$ReleaseCandidate
    }
    $state='SUCCESS'; $resultCode=0
} catch {
    $detail=(Protect-GiteeDiagnostic ([string]$_.Exception.Message)) -replace '[\r\n]+',' '
    $state='FAILED'; Write-Host ('GITEE_MIRROR_FAILED: '+$detail) -ForegroundColor Red
} finally {
    Remove-Item Env:GITEE_TOKEN -ErrorAction SilentlyContinue
    if ($secure) { $secure.Dispose() }
    Write-SecureMirrorStatus
    Write-Host ('GITEE_MIRROR_STATUS='+$state+' credential_cleared='+(-not [bool]$env:GITEE_TOKEN))
}
exit $resultCode
