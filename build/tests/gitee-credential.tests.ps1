$ErrorActionPreference='Stop'
if ($env:GITEE_TOKEN) { throw 'Credential regression requires an environment without a real runtime token.' }
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'scripts\Get-GiteeCredential.ps1') -LoadOnly
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('netboot-credential-test-'+[guid]::NewGuid().ToString('N'))
$store=Join-Path $testRoot 'Secrets\gitee-token.dpapi'
$fake='netboot-test-only-'+[guid]::NewGuid().ToString('N')
$credential=ConvertTo-SecureString $fake -AsPlainText -Force
$script:credentialQueryStatus=200
$script:credentialQueryCount=0
$checks=0
function Assert-CredentialTest([bool]$Condition,[string]$Message) {
    if(-not $Condition){throw $Message};$script:checks++
}
function Invoke-GiteeCredentialQuery {
    param([Security.SecureString]$Credential,[string]$RelativePath)
    $script:credentialQueryCount++
    return [pscustomobject]@{HttpStatus=$script:credentialQueryStatus;Record=[pscustomobject]@{login='test-account'}}
}
try {
    $missing=$false
    try{$null=Get-GiteeCredential -CredentialPath $store}catch{$missing=$_.Exception.Message -match 'not initialized'}
    Assert-CredentialTest $missing 'A noninteractive missing credential must fail without prompting.'
    $set=Set-GiteeCredential -Credential $credential -CredentialPath $store
    Assert-CredentialTest ($set.ValidationPassed -and -not $set.Reused) 'First safe input must be saved only after read-only validation.'
    $cipher=[IO.File]::ReadAllText($store)
    Assert-CredentialTest ($cipher.StartsWith('01000000d08c9ddf') -and -not $cipher.Contains($fake)) 'Credential file must contain DPAPI ciphertext only.'
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $acl=Get-Acl -LiteralPath $store
    Assert-CredentialTest ($acl.AreAccessRulesProtected -and @($acl.Access).Count -eq 1 -and $acl.Access[0].IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $sid) 'Credential ACL must grant access only to the current Windows SID.'
    $loaded=Get-GiteeCredential -CredentialPath $store
    try{Assert-CredentialTest ($loaded -is [Security.SecureString] -and $loaded.Length -eq $credential.Length) 'Get must return SecureString, never plaintext.'}finally{$loaded.Dispose()}
    $runner=(Get-Command pwsh -ErrorAction Stop).Source
    $helper=(Join-Path $repo 'scripts\Get-GiteeCredential.ps1').Replace("'","''")
    $safeStore=$store.Replace("'","''")
    $child=". '$helper' -LoadOnly; `$ss=Get-GiteeCredential -CredentialPath '$safeStore'; try { if (`$ss.Length -ne $($credential.Length)) {exit 1}; Write-Output 'DPAPI_CHILD_REUSE_OK' } finally {`$ss.Dispose()}"
    $output=& $runner -NoProfile -NonInteractive -Command $child
    Assert-CredentialTest ($LASTEXITCODE -eq 0 -and $output -contains 'DPAPI_CHILD_REUSE_OK') 'A fresh PowerShell process must reuse current-user DPAPI without input.'
    $before=(Get-FileHash -LiteralPath $store).Hash
    $reuse=Set-GiteeCredential -Credential $credential -CredentialPath $store
    Assert-CredentialTest ($reuse.Reused -and (Get-FileHash -LiteralPath $store).Hash -ceq $before) 'Repeated initialization must reuse ciphertext without rewriting it.'
    $test=Test-GiteeCredential -CredentialPath $store
    Assert-CredentialTest ($test.Success -and $test.HttpStatus -eq 200) 'Account/repository/release read-only validation must report success.'
    $script:credentialQueryStatus=401
    $rejected=Test-GiteeCredential -CredentialPath $store
    Assert-CredentialTest (-not $rejected.Success -and $rejected.Reason -match 'AuthenticationRejected') 'Explicit authentication failure must require manual reconfiguration.'
    $replaceRejected=$false
    try{$null=Set-GiteeCredential -Credential $credential -CredentialPath $store -Replace}catch{$replaceRejected=$true}
    Assert-CredentialTest ($replaceRejected -and (Get-FileHash -LiteralPath $store).Hash -ceq $before) 'Invalid replacement must retain the original encrypted credential.'
    $script:credentialQueryStatus=503
    $unavailable=Test-GiteeCredential -CredentialPath $store
    Assert-CredentialTest (-not $unavailable.Success -and $unavailable.Reason -match 'APIUnavailable') 'Service failure must not be interpreted as token expiry.'
    $script:credentialQueryStatus=200
    $result=Invoke-WithGiteeCredential -CredentialPath $store -Operation {
        if($env:GITEE_TOKEN -cne $fake){throw 'DPAPI content mismatch'}
        'OPERATION_OK'
    }
    Assert-CredentialTest ($result -eq 'OPERATION_OK' -and -not (Test-Path Env:GITEE_TOKEN)) 'Runtime success must decrypt correctly and remove its process variable.'
    $caught=''
    try{Invoke-WithGiteeCredential -CredentialPath $store -Operation {throw ('request access_token='+$env:GITEE_TOKEN+' Authorization: Bearer '+$env:GITEE_TOKEN)}}catch{$caught=$_.Exception.Message}
    Assert-CredentialTest ($caught.Contains('[redacted]') -and -not $caught.Contains($fake) -and -not (Test-Path Env:GITEE_TOKEN)) 'Failure must redact credentials and clean its process variable.'
    foreach($sample in @('Authorization: Basic abc123','{"Authorization":"Bearer abc123"}','https://gitee.com/api?access_token=abc123&x=1','{"access_token":"abc123"}')){
        $safe=Protect-GiteeDiagnostic $sample
        Assert-CredentialTest (-not $safe.Contains('abc123')) 'Headers/query/JSON credential values must be redacted.'
        Assert-GiteeCredentialFreeText $safe
    }
    $blocked=$false
    try{Assert-GiteeCredentialFreeText ('secret='+$fake) @($fake)}catch{$blocked=$true}
    Assert-CredentialTest $blocked 'Diagnostic writes containing a token must hard fail.'
    $exposurePath=Join-Path $testRoot 'exposure-test.log'
    [IO.File]::WriteAllText($exposurePath,$fake)
    $exposureBlocked=$false
    try{$null=Test-GiteeCredentialExposure -CredentialPath $store -Paths @($exposurePath)}catch{$exposureBlocked=$_.Exception.Message -match 'exposure detected'}
    Assert-CredentialTest $exposureBlocked 'The exposure audit must detect a token written to a diagnostic file.'
    [IO.File]::WriteAllText($exposurePath,'safe diagnostic text')
    $clean=Test-GiteeCredentialExposure -CredentialPath $store -Paths @($exposurePath)
    Assert-CredentialTest ($clean.Passed -and $clean.FilesChecked -eq 1 -and $clean.ReadableCommandLinesChecked -gt 0) 'The exposure audit must scan diagnostic files and readable process command lines without printing secrets.'
    $repoRejected=$false
    try{$null=Resolve-GiteeCredentialPath (Join-Path $repo 'gitee-token.dpapi')}catch{$repoRejected=$true}
    Assert-CredentialTest $repoRejected 'Encrypted credentials must also be rejected inside the repository.'
    $invalidStore=Join-Path $testRoot 'invalid.dpapi'
    [IO.File]::WriteAllText($invalidStore,'invalid-encrypted-data')
    $corruptRejected=$false
    try{$null=Get-GiteeCredential -CredentialPath $invalidStore -AllowPrompt}catch{$corruptRejected=$_.Exception.Message -match 'cannot be decrypted'}
    Assert-CredentialTest ($corruptRejected -and [IO.File]::ReadAllText($invalidStore) -ceq 'invalid-encrypted-data') 'Unreadable DPAPI must not trigger replacement/input automatically.'
    $removed=Remove-GiteeCredential -CredentialPath $store
    Assert-CredentialTest ($removed.Removed -and -not $removed.RemoteTokenRevoked -and -not (Test-Path Env:GITEE_TOKEN)) 'Removal must delete only local ciphertext and never revoke account tokens.'
    Write-Output "GITEE_CREDENTIAL_TESTS_OK checks=$checks current_user_dpapi=1 cross_process_reuse=1 private_acl=1 redaction=1 finally_cleanup=1 no_auto_replacement=1"
} finally {
    $credential.Dispose();Remove-Item Env:GITEE_TOKEN -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($testRoot)
    $base=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
    if([IO.Path]::GetDirectoryName($resolved) -ceq $base -and [IO.Path]::GetFileName($resolved).StartsWith('netboot-credential-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
