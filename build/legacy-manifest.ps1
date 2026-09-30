function Copy-NetBootFrozenLegacyManifest {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$Destination)
    $expected = @{
        'latest.json' = '10d2b102b615c007e81616b9abe15c939b43f212e9667910822b78975e9a48a7'
        'latest.json.sig' = '90e6614e26ccbc7e7d5f54210e3caad1ddc4e8ddecefc527ee793bfc4e9af8e9'
    }
    foreach ($name in $expected.Keys) {
        $source = Join-Path $RepositoryRoot "build\legacy\v1.0.20\$name"
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[$name]) {
            throw "Frozen v1.0.20 $name was altered; refusing to publish a legacy manifest."
        }
        $target = Join-Path $Destination $name
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[$name]) {
                throw "Destination already contains a different legacy $name; refusing to overwrite it."
            }
        } else { Copy-Item -LiteralPath $source -Destination $target }
    }
}
