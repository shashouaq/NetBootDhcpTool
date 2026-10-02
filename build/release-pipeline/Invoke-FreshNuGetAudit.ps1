[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$RepositoryRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$DotnetPath='dotnet')
$ErrorActionPreference='Stop'
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $evidence){throw 'Fresh audit evidence directory must be new; previous evidence is preserved.'}
New-Item -ItemType Directory -Path $evidence|Out-Null
$previousCache=$env:NUGET_HTTP_CACHE_PATH
try {
    # Isolate the audit HTTP cache; never reuse a previous vulnerability response.
    $env:NUGET_HTTP_CACHE_PATH=Join-Path $evidence 'http-cache'
    Push-Location $RepositoryRoot
    try {
        [DateTimeOffset]::UtcNow.ToString('O')|Set-Content (Join-Path $evidence 'timestamp.txt')
        & $DotnetPath --info *> (Join-Path $evidence 'sdk.txt')
        if($LASTEXITCODE){throw 'Could not identify the audit SDK.'}
        $solution=Join-Path $RepositoryRoot 'NetBootDhcpTool.sln'
        $feed='https://api.nuget.org/v3/index.json'
        $restoreLog=Join-Path $evidence 'restore.log'
        & $DotnetPath restore $solution --force-evaluate --no-http-cache --source $feed '-p:NuGetAudit=true' '-p:NuGetAuditMode=all' '-p:NuGetAuditLevel=low' *> $restoreLog
        if($LASTEXITCODE -ne 0 -or (Select-String -LiteralPath $restoreLog -Pattern 'NU190[0-5]' -Quiet)){throw 'Fresh vulnerability audit failed or its official feed was unavailable.'}
        & $DotnetPath list $solution package --include-transitive --format json > (Join-Path $evidence 'package-graph.json')
        if($LASTEXITCODE){throw 'Could not record the dependency graph.'}
        $reportPath=Join-Path $evidence 'vulnerabilities.json'
        & $DotnetPath list $solution package --vulnerable --include-transitive --source $feed --format json > $reportPath
        if($LASTEXITCODE){throw 'Official vulnerability query failed.'}
        $report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
        if($report.errors){throw 'The official vulnerability query reported errors.'}
        $expected=@(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src') -Recurse -Filter '*.csproj').FullName
        $actual=@($report.projects|ForEach-Object {[IO.Path]::GetFullPath($_.path)})
        if(@($expected|Where-Object {$_ -notin $actual}).Count){throw 'Vulnerability query did not cover every source project.'}
        foreach($project in $report.projects){
            if(-not @($project.frameworks|Where-Object {$_}).Count){throw 'Vulnerability query omitted framework coverage.'}
        }
        foreach($framework in $report.projects.frameworks){
            if(@($framework.topLevelPackages|Where-Object {$_}).Count -or @($framework.transitivePackages|Where-Object {$_}).Count){throw 'Vulnerability query reported vulnerable packages.'}
        }
        @{Passed=$true;OfficialFeed=$feed;CoveredSourceProjects=$expected.Count;FreshHttpCache=$true;NuGetAuditMode='all'}|ConvertTo-Json|Set-Content (Join-Path $evidence 'summary.json')
        Write-Output "FRESH_NUGET_AUDIT_OK projects=$($expected.Count) official_feed=1 fresh_http_cache=1"
    } finally {Pop-Location}
} finally {
    if($null -eq $previousCache){Remove-Item Env:NUGET_HTTP_CACHE_PATH -ErrorAction SilentlyContinue}else{$env:NUGET_HTTP_CACHE_PATH=$previousCache}
}
