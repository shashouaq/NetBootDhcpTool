[CmdletBinding()]
param([string]$AssetDirectory,[string]$Version)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'build/pe-version.ps1')
foreach($inputVersion in @('1.1.1','1.1.1-rc.1','1.1.1-rc.1+test')){
    $value=Get-NetBootVersionMetadata -Version $inputVersion
    if($value.NumericVersion -cne '1.1.1.0' -or $value.Version -cne $inputVersion){throw 'SemVer conversion failed'}
}
foreach($bad in @('1.1','01.1.1','1.1.1-01','65535.1.1','1.1.1.0')){
    $failed=$false;try{$null=Get-NetBootVersionMetadata -Version $bad}catch{$failed=$true};if(-not $failed){throw "Invalid version accepted: $bad"}
}
if($AssetDirectory){
    Assert-NetBootReleaseVersions -Directory $AssetDirectory -Version $Version
}else{
    # Compile a real small installer with the production NSIS script and its resource declarations.
    $compiler=if($env:MAKENSIS_PATH){$env:MAKENSIS_PATH}else{(Get-Command makensis.exe -ErrorAction SilentlyContinue).Source}
    if(-not $compiler){throw 'Set MAKENSIS_PATH to NSIS 3.12 to run real PE resource tests.'}
    $temp=Join-Path $env:TEMP ('netboot-pe-test-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
    try{
        $helper=Join-Path $temp 'helper.exe';$manifest=Join-Path $temp 'manifest.json';$sig=Join-Path $temp 'manifest.sig'
        [IO.File]::WriteAllText($helper,'fixture');[IO.File]::WriteAllText($manifest,'fixture');[IO.File]::WriteAllText($sig,'fixture')
        foreach($v in @('1.1.1','1.1.1-rc.1')){
            $metadata=Get-NetBootVersionMetadata -Version $v
            & $compiler "-DPRODUCT_VERSION=$v" "-DPE_VERSION=$($metadata.NumericVersion)" "-DPRODUCT_NAME=$($metadata.ProductName)" "-DCOMPANY_NAME=$($metadata.CompanyName)" "-DPRODUCT_COPYRIGHT=$($metadata.Copyright)" "-DOUTPUT_DIRECTORY=$temp" "-DSETUP_HELPER=$helper" "-DMANIFEST=$manifest" "-DSIGNATURE=$sig" '-DUSER_CANCELLED_EXIT_CODE=10' (Join-Path $repo 'installer/NetBootDhcpTool.Setup.nsi') *> $null
            if($LASTEXITCODE -ne 0){throw 'NSIS version fixture build failed'}
            $null=Assert-NetBootPEVersion -Path (Join-Path $temp "NetBootDhcpTool-Setup-v$v.exe") -Version $v -Description 'NetBoot DHCP Tool Setup'
        }
        $failed=$false;try{$null=Assert-NetBootPEVersion -Path (Join-Path $temp 'NetBootDhcpTool-Setup-v1.1.1.exe') -Version '1.1.2'}catch{$failed=$true}
        if(-not $failed){throw 'PE mismatch gate did not fail'}
    }finally{if((Split-Path -Leaf $temp) -notlike 'netboot-pe-test-*'){throw 'Unsafe test cleanup path'};Remove-Item -LiteralPath $temp -Recurse -Force}
}
Write-Output 'PE_VERSION_TESTS_OK'
