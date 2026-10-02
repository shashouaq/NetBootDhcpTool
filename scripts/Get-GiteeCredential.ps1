[CmdletBinding()]
param(
    [Alias('Action')][ValidateSet('Get', 'Set', 'Test', 'Remove')][string]$ManageAction = 'Test',
    [Alias('CredentialPath')][string]$StorePath,
    [Alias('Replace')][switch]$ReplaceStored,
    [switch]$LoadOnly
)

# Dot-source this file to use the functions. No function returns a plaintext token.
$script:GiteeCredentialRepositoryRoot = Split-Path -Parent $PSScriptRoot

function Resolve-GiteeCredentialPath {
    param([string]$Path)
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Gitee credential storage requires Windows current-user DPAPI.' }
    if (-not $Path) { $Path = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NetBootDhcpTool\Secrets\gitee-token.dpapi' }
    $full = [IO.Path]::GetFullPath($Path)
    $repo = [IO.Path]::GetFullPath($script:GiteeCredentialRepositoryRoot).TrimEnd('\','/')
    if ($full.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $full -ieq $repo) {
        throw 'Credential storage inside the project repository is forbidden.'
    }
    $current = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($full))
    while ($current) {
        if ($current.Exists -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Credential storage must not cross a reparse point.' }
        if (Test-Path -LiteralPath (Join-Path $current.FullName '.git')) { throw 'Credential storage inside any Git checkout is forbidden.' }
        $current = $current.Parent
    }
    if ([IO.File]::Exists($full) -and ([IO.File]::GetAttributes($full) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Credential file must not be a reparse point.' }
    return $full
}

function Protect-GiteeDiagnostic {
    param([AllowEmptyString()][string]$Text, [string[]]$Secrets = @())
    $result = [string]$Text
    foreach ($secret in @($Secrets) + @($env:GITEE_TOKEN, $env:GITHUB_TOKEN, $env:GH_TOKEN)) {
        if (-not [string]::IsNullOrEmpty($secret)) {
            $result = $result.Replace($secret, '[redacted]').Replace([uri]::EscapeDataString($secret), '[redacted]')
        }
    }
    $result = $result -replace '(?im)(\bauthorization\b\s*["'']?\s*[:=]\s*["'']?)[^\r\n,}]+', '$1[redacted]'
    $result = $result -replace '(?i)(\baccess_token\b["'']?\s*[:=]\s*["'']?)[^&\s"''},]+', '$1[redacted]'
    return $result
}

function Assert-GiteeCredentialFreeText {
    param([AllowEmptyString()][string]$Text, [string[]]$Secrets = @())
    foreach ($secret in @($Secrets) + @($env:GITEE_TOKEN)) {
        if ($secret -and ($Text.Contains($secret) -or $Text.Contains([uri]::EscapeDataString($secret)))) {
            throw 'Credential exposure was blocked before diagnostic output or file write.'
        }
    }
    if ($Text -match '(?i)\bauthorization\b\s*["'']?\s*[:=]\s*["'']?(?:Bearer|Basic)\s+(?!\[redacted\])\S+' -or
        $Text -match '(?i)\baccess_token\b["'']?\s*[:=]\s*["'']?(?!\[redacted\])[^&\s"''},]+') {
        throw 'Authorization/access_token values are forbidden in diagnostics.'
    }
}

function Invoke-GiteeCredentialQuery {
    param([Security.SecureString]$Credential, [string]$RelativePath)
    if ($RelativePath -notmatch '^/(user|repos/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/releases\?per_page=1)?)$') { throw 'Credential test only permits read-only Gitee account/repository queries.' }
    $bstr = [IntPtr]::Zero; $plain = $null; $client = $null; $handler = $null; $response = $null
    try {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Credential)
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        if ([string]::IsNullOrWhiteSpace($plain) -or $plain -match '[\r\n\x00]') { throw 'Gitee credential is empty or malformed; configure it explicitly.' }
        Import-Module (Join-Path $script:GiteeCredentialRepositoryRoot 'build/release-pipeline/ReleaseTransport.psm1')
        $response = Invoke-ReleaseHttp -Uri ('https://gitee.com/api/v5' + $RelativePath) -Headers @{Authorization=('Bearer '+$plain);'User-Agent'='NetBootDhcpTool-CredentialCheck/1.0'} -MaximumRedirection 0 -Stage credential-read
        $status = [int]$response.StatusCode
        $record = $null
        if ($status -eq 200) {
            try { $record = $response.Content | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Gitee credential query returned invalid JSON.' }
        }
        return [pscustomobject]@{ HttpStatus=$status; Record=$record }
    } catch {
        throw 'Gitee credential query could not complete. Check connectivity; the saved credential was not replaced or deleted.'
    } finally {
        if ($client) { $client.Dispose() } elseif ($handler) { $handler.Dispose() }
        if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        $plain = $null
    }
}

function Test-GiteeCredential {
    [CmdletBinding()]
    param([Security.SecureString]$Credential, [string]$CredentialPath, [string]$Repository = 'joel20230302/NetBootDhcpTool')
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid Gitee repository identity.' }
    $owned = $false
    try {
        if (-not $Credential) { $Credential = Get-GiteeCredential -CredentialPath $CredentialPath; $owned=$true }
        $account = Invoke-GiteeCredentialQuery $Credential '/user'
        if ($account.HttpStatus -ne 200) {
            $reason = if ($account.HttpStatus -in @(400,401,403)) { 'AuthenticationRejected: reconfigure the existing dedicated credential explicitly; no token will be created automatically.' } else { 'APIUnavailable: retain the saved token and retry when the service is available.' }
            return [pscustomobject]@{ Success=$false; HttpStatus=$account.HttpStatus; Repository=$Repository; Reason=$reason }
        }
        $repo = Invoke-GiteeCredentialQuery $Credential ('/repos/' + $Repository)
        $releaseRead = if ($repo.HttpStatus -eq 200) { Invoke-GiteeCredentialQuery $Credential ('/repos/' + $Repository + '/releases?per_page=1') } else { $repo }
        $success = $repo.HttpStatus -eq 200 -and $releaseRead.HttpStatus -eq 200
        return [pscustomobject]@{ Success=$success; HttpStatus=$releaseRead.HttpStatus; Repository=$Repository;
            Reason=$(if ($success) { 'Account and release-read queries passed. Write permissions remain enforced by Gitee during publication.' } elseif ($releaseRead.HttpStatus -in @(401,403)) { 'RepositoryPermissionDenied: explicitly check the dedicated token permission; no automatic replacement.' } else { 'Repository/API unavailable; the stored credential was retained.' }) }
    } finally { if ($owned -and $Credential) { $Credential.Dispose() } }
}

function Set-GiteeCredential {
    [CmdletBinding()]
    param([Security.SecureString]$Credential, [string]$CredentialPath, [switch]$Replace)
    $path = Resolve-GiteeCredentialPath $CredentialPath
    if ([IO.File]::Exists($path) -and -not $Replace) {
        $check = Test-GiteeCredential -CredentialPath $path
        if (-not $check.Success) { throw $check.Reason }
        return [pscustomobject]@{ Path=$path; Reused=$true; ValidationPassed=$true }
    }
    $owned = $false; $temporary = $null
    try {
        if (-not $Credential) { $Credential = Read-Host 'Existing dedicated Gitee Token (hidden, saved with current-user DPAPI)' -AsSecureString; $owned=$true }
        if ($Credential.Length -eq 0) { throw 'No credential entered; existing storage is unchanged.' }
        $check = Test-GiteeCredential -Credential $Credential
        if (-not $check.Success) { throw $check.Reason }
        $cipher = ConvertFrom-SecureString -SecureString $Credential
        $parent = [IO.Path]::GetDirectoryName($path)
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $directoryAcl = [Security.AccessControl.DirectorySecurity]::new()
        $directoryAcl.SetOwner($sid); $directoryAcl.SetAccessRuleProtection($true,$false)
        $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        Set-Acl -LiteralPath $parent -AclObject $directoryAcl
        $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.partial'
        [IO.File]::WriteAllText($temporary, $cipher, [Text.UTF8Encoding]::new($false))
        $roundTrip = ConvertTo-SecureString ([IO.File]::ReadAllText($temporary))
        try { if ($roundTrip.Length -ne $Credential.Length) { throw 'DPAPI round-trip failed.' } } finally { $roundTrip.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $path -Force
        $fileAcl = [Security.AccessControl.FileSecurity]::new()
        $fileAcl.SetOwner($sid); $fileAcl.SetAccessRuleProtection($true,$false)
        $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow'))
        Set-Acl -LiteralPath $path -AclObject $fileAcl
        return [pscustomobject]@{ Path=$path; Reused=$false; ValidationPassed=$true }
    } finally {
        if ($temporary -and [IO.File]::Exists($temporary)) { Remove-Item -LiteralPath $temporary -Force }
        if ($owned -and $Credential) { $Credential.Dispose() }
        $cipher=$null
    }
}

function Get-GiteeCredential {
    [CmdletBinding()]
    param([string]$CredentialPath, [switch]$AllowPrompt)
    $path = Resolve-GiteeCredentialPath $CredentialPath
    if ([IO.File]::Exists($path)) {
        try { return ConvertTo-SecureString ([IO.File]::ReadAllText($path)) -ErrorAction Stop }
        catch { throw 'Saved Gitee DPAPI credential cannot be decrypted by this Windows user. Explicitly reconfigure it; no automatic replacement.' }
    }
    if ($env:GITEE_TOKEN) { return ConvertTo-SecureString -String $env:GITEE_TOKEN -AsPlainText -Force }
    if ($AllowPrompt) { $null = Set-GiteeCredential -CredentialPath $path; return Get-GiteeCredential -CredentialPath $path }
    throw 'Gitee credential is not initialized. Run Set-GiteeCredential once in a secure interactive PowerShell window; reuse the existing dedicated token.'
}

function Remove-GiteeCredential {
    [CmdletBinding()]
    param([string]$CredentialPath)
    $path = Resolve-GiteeCredentialPath $CredentialPath
    if ([IO.File]::Exists($path)) { Remove-Item -LiteralPath $path -Force }
    Remove-Item Env:GITEE_TOKEN -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Path=$path; Removed=(-not [IO.File]::Exists($path)); RemoteTokenRevoked=$false }
}

function Invoke-WithGiteeCredential {
    [CmdletBinding()]
    param([Parameter(Mandatory)][scriptblock]$Operation, [string]$CredentialPath, [switch]$AllowPrompt)
    $secure = $null; $bstr=[IntPtr]::Zero; $plain=$null
    try {
        $secure = Get-GiteeCredential -CredentialPath $CredentialPath -AllowPrompt:$AllowPrompt
        $check = Test-GiteeCredential -Credential $secure
        if (-not $check.Success) { throw $check.Reason }
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        $env:GITEE_TOKEN = $plain
        & $Operation
    } catch {
        $safe = Protect-GiteeDiagnostic ([string]$_.Exception.Message) @($plain)
        Assert-GiteeCredentialFreeText $safe @($plain)
        throw $safe
    } finally {
        Remove-Item Env:GITEE_TOKEN -ErrorAction SilentlyContinue
        if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        if ($secure) { $secure.Dispose() }; $plain=$null
    }
}

function Test-GiteeCredentialExposure {
    [CmdletBinding()]
    param([string[]]$Paths, [string]$CredentialPath)
    $ErrorActionPreference='Stop'
    $secure=$null; $bstr=[IntPtr]::Zero; $plain=$null; $basic=$null; $bytes=$null
    $filesChecked=0; $processesChecked=0
    try {
        $secure=Get-GiteeCredential -CredentialPath $CredentialPath
        $bstr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        $plain=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        $bytes=[Text.Encoding]::UTF8.GetBytes('joel20230302:'+$plain)
        $basic=[Convert]::ToBase64String($bytes)
        foreach($path in $Paths) {
            if(-not [IO.File]::Exists($path)){continue}
            $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true)
            try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
            foreach($value in @($plain,[uri]::EscapeDataString($plain),$basic)) {
                if($text.Contains($value)){throw ('Credential exposure detected in file: '+$path)}
            }
            $filesChecked++
        }
        foreach($process in (Get-CimInstance Win32_Process -ErrorAction Stop)) {
            if(-not $process.CommandLine){continue}
            foreach($value in @($plain,[uri]::EscapeDataString($plain),$basic)) {
                if($process.CommandLine.Contains($value)){throw ('Credential exposure detected in process command line, PID='+$process.ProcessId)}
            }
            $processesChecked++
        }
        return [pscustomobject]@{Passed=$true;FilesChecked=$filesChecked;ReadableCommandLinesChecked=$processesChecked}
    } finally {
        if($bstr -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)}
        if($secure){$secure.Dispose()};if($bytes){[Array]::Clear($bytes,0,$bytes.Length)};$plain=$null;$basic=$null
    }
}

if (-not $LoadOnly -and $MyInvocation.InvocationName -ne '.') {
    switch ($ManageAction) {
        'Set' { Set-GiteeCredential -CredentialPath $StorePath -Replace:$ReplaceStored }
        'Test' { Test-GiteeCredential -CredentialPath $StorePath }
        'Remove' { Remove-GiteeCredential -CredentialPath $StorePath }
        'Get' { Get-GiteeCredential -CredentialPath $StorePath }
    }
}
