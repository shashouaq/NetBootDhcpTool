. (Join-Path $PSScriptRoot 'release-identity.ps1')

function Assert-NetBootPEVersion {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Version, [string]$Description)
    $expected = Get-NetBootVersionMetadata -Version $Version
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($Path))
    $fileNumeric = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart,$info.FilePrivatePart
    $productNumeric = '{0}.{1}.{2}.{3}' -f $info.ProductMajorPart,$info.ProductMinorPart,$info.ProductBuildPart,$info.ProductPrivatePart
    if ($fileNumeric -cne $expected.NumericVersion -or $productNumeric -cne $expected.NumericVersion -or
        $info.FileVersion -cne $expected.NumericVersion -or $info.ProductVersion -cne $Version -or
        $info.ProductName -cne $expected.ProductName -or [string]::IsNullOrWhiteSpace($info.FileDescription) -or
        $info.CompanyName -cne $expected.CompanyName -or $info.LegalCopyright -cne $expected.Copyright -or
        ($Description -and $info.FileDescription -cne $Description)) {
        throw "PE version/identity mismatch: $([IO.Path]::GetFileName($Path)); expected $Version / $($expected.NumericVersion)."
    }
    return [pscustomobject]@{Name=[IO.Path]::GetFileName($Path);FileVersion=$info.FileVersion;ProductVersion=$info.ProductVersion;ProductName=$info.ProductName;FileDescription=$info.FileDescription;CompanyName=$info.CompanyName;Copyright=$info.LegalCopyright}
}

function Assert-NetBootReleaseVersions {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Version, [string]$SevenZipPath = 'C:\Program Files\7-Zip\7z.exe')
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'latest-v2.json') -Raw | ConvertFrom-Json
    $full = @($manifest.sevenZipPackages | Where-Object kind -CEQ 'Full')
    if ($manifest.version -cne $Version -or $manifest.archiveName -cne "NetBootDhcpTool-v$Version.7z" -or
        $full.Count -ne 1 -or $full[0].fileName -cne "NetBootDhcpTool-full-v$Version.7z") { throw 'Release manifest/filename version mismatch.' }
    $null = Assert-NetBootPEVersion -Path (Join-Path $Directory "NetBootDhcpTool-Setup-v$Version.exe") -Version $Version -Description 'NetBoot DHCP Tool Setup'
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('netboot-version-gate-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    try {
        foreach ($entry in @(@{Name=$manifest.archiveName;Root='NetBootDhcpTool';Label='portable'},@{Name=$full[0].fileName;Root='payload';Label='full'})) {
            $target = Join-Path $tempRoot $entry.Label
            & $SevenZipPath x (Join-Path $Directory $entry.Name) "-o$target" -y *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Version gate archive extraction failed.' }
            $payload = Join-Path $target $entry.Root
            $null = Assert-NetBootPEVersion -Path (Join-Path $payload 'NetBootDhcpTool.exe') -Version $Version -Description 'NetBoot DHCP Tool'
            $null = Assert-NetBootPEVersion -Path (Join-Path $payload 'NetBootDhcpTool.Updater.exe') -Version $Version -Description 'NetBoot DHCP Tool Updater'
            $install = Get-Content -LiteralPath (Join-Path $payload 'install-manifest.json') -Raw | ConvertFrom-Json
            if ($install.version -cne $Version) { throw 'Install manifest version mismatch.' }
            if ($entry.Label -ceq 'full') {
                $package = Get-Content -LiteralPath (Join-Path $target 'update-package.json') -Raw | ConvertFrom-Json
                if ($package.targetVersion -cne $Version) { throw 'Package manifest version mismatch.' }
            }
        }
        Write-Host "RELEASE_VERSION_GATE_OK version=$Version app/updater/setup/portable/full/manifests"
    } finally {
        # This root was created above and never comes from a caller or archive path.
        if ((Split-Path -Leaf $tempRoot) -notlike 'netboot-version-gate-*') { throw 'Unexpected version gate cleanup path.' }
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
