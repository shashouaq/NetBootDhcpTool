$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$root=[IO.Path]::GetFullPath((Join-Path $tempRoot ('netboot-audit-test-'+[guid]::NewGuid().ToString('N'))))
$previousCache=$env:NUGET_HTTP_CACHE_PATH
$previousFixture=Get-Variable -Name NetBootAuditFixtureState -Scope Global -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $root 'src')|Out-Null
$project=Join-Path $root 'src/fixture.csproj'
[IO.File]::WriteAllText($project,'<Project />')
[IO.File]::WriteAllText((Join-Path $root 'NetBootDhcpTool.sln'),'fixture')
function Invoke-FixtureAuditDotnet {
    $global:LASTEXITCODE=0
    if($args[0] -eq '--info'){return 'isolated fake SDK'}
    if($args[0] -eq 'restore'){
        if($args -notcontains '--no-http-cache' -or $args -notcontains '--force-evaluate' -or $args -notcontains 'https://api.nuget.org/v3/index.json' -or $args -notcontains '-p:NuGetAuditMode=all'){throw ("Audit weakened source/freshness/transitive coverage: " + ($args -join "|"))}
        if($env:NUGET_HTTP_CACHE_PATH -cne (Join-Path $global:NetBootAuditFixtureState.Evidence 'http-cache')){throw 'Audit cache not isolated'}
        if($global:NetBootAuditFixtureState.Mode -eq 'restore-exit'){$global:LASTEXITCODE=1;return 'restore failed'}
        if($global:NetBootAuditFixtureState.Mode -eq 'feed'){return 'warning NU1900: feed unavailable'}
        return 'restore passed'
    }
    $framework=@{framework='net10.0';topLevelPackages=@();transitivePackages=@()}
    if($args -notcontains '--vulnerable'){
        $graph=@{projects=@(@{path=$project;frameworks=@($framework)})}
        if($global:NetBootAuditFixtureState.Mode -eq 'framework'){$graph.projects[0].frameworks=@()}
        if($global:NetBootAuditFixtureState.Mode -eq 'graph-coverage'){$graph.projects=@()}
        if($global:NetBootAuditFixtureState.Mode -eq 'graph-errors'){$graph.errors=@('dependency graph failed')}
        return ($graph|ConvertTo-Json -Depth 8)
    }
    if($global:NetBootAuditFixtureState.Mode -eq 'vulnerable'){$framework.transitivePackages=@(@{id='unsafe-fixture';resolvedVersion='1.0.0'})}
    # Real healthy --vulnerable JSON retains the project path but omits frameworks.
    $data=@{projects=@(@{path=$project})}
    if($global:NetBootAuditFixtureState.Mode -in @('vulnerable','empty-filtered')){$data.projects[0].frameworks=@($framework)}
    if($global:NetBootAuditFixtureState.Mode -eq 'errors'){$data.errors=@('official query unavailable')}
    if($global:NetBootAuditFixtureState.Mode -eq 'coverage'){$data.projects=@(@{path=(Join-Path $root 'missing.csproj');frameworks=@($framework)})}
    if($global:NetBootAuditFixtureState.Mode -eq 'query-exit'){$global:LASTEXITCODE=1;return 'query failed'}
    return ($data|ConvertTo-Json -Depth 8)
}
try {
    $cases=0
    foreach($mode in @('healthy','empty-filtered','feed','restore-exit','vulnerable','errors','coverage','framework','graph-coverage','graph-errors','query-exit')){
        $global:NetBootAuditFixtureState=@{Mode=$mode;Evidence=(Join-Path $root $mode)}
        $env:NUGET_HTTP_CACHE_PATH='fixture-previous-cache'
        $failed=$false;$detail=''
        try{& (Join-Path $repo 'build/release-pipeline/Invoke-FreshNuGetAudit.ps1') -RepositoryRoot $root -EvidenceDirectory $global:NetBootAuditFixtureState.Evidence -DotnetPath 'Invoke-FixtureAuditDotnet'}catch{$failed=$true;$detail=$_.Exception.Message}
        $healthy=$mode -in @('healthy','empty-filtered')
        if($healthy -eq $failed){throw "Unexpected audit decision for ${mode}: $detail"}
        $expectedError=@{feed='official feed was unavailable';'restore-exit'='official feed was unavailable';vulnerable='vulnerable packages';errors='reported errors';coverage='did not cover';framework='omitted framework';'graph-coverage'='did not cover';'graph-errors'='reported errors';'query-exit'='query failed'}
        if($failed -and $detail -notmatch $expectedError[$mode]){throw "Wrong failure reason for ${mode}: $detail"}
        if($env:NUGET_HTTP_CACHE_PATH -cne 'fixture-previous-cache'){throw 'Audit leaked cache environment'}
        if(-not $healthy -and (Test-Path (Join-Path $global:NetBootAuditFixtureState.Evidence 'summary.json'))){throw 'Failed audit emitted success evidence'}
        $cases++
    }
    Write-Output "NUGET_AUDIT_TESTS_OK cases=$cases official_source=1 fresh_cache=1 transitive=1 feed_failure_hard_fail=1"
}finally{
    if($null -eq $previousCache){Remove-Item Env:NUGET_HTTP_CACHE_PATH -ErrorAction SilentlyContinue}else{$env:NUGET_HTTP_CACHE_PATH=$previousCache}
    if($previousFixture){Set-Variable -Name NetBootAuditFixtureState -Scope Global -Value $previousFixture.Value}else{Remove-Variable -Name NetBootAuditFixtureState -Scope Global -ErrorAction SilentlyContinue}
    if([IO.Path]::GetDirectoryName($root) -cne $tempRoot -or (Split-Path -Leaf $root) -notlike 'netboot-audit-test-*'){throw 'Unsafe audit fixture cleanup path'}
    Remove-Item -LiteralPath $root -Recurse -Force
}
