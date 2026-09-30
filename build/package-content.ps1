$script:ProductDocumentationAllowlist = @(
    'README.md',
    'docs/FEATURE_CHANGELOG.md',
    'docs/THIRD_PARTY_NOTICES.md',
    'docs/NetBootDhcpTool_OnePageGuide.html',
    'docs/RELEASE_NOTES.md',
    'docs/troubleshooting/windows-wifi-wired-conflict.md'
)

function Copy-ProductDocumentation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DestinationProduct
    )

    $source = [System.IO.Path]::GetFullPath($SourceRoot)
    $destination = [System.IO.Path]::GetFullPath($DestinationProduct)
    foreach ($relativePath in $script:ProductDocumentationAllowlist) {
        $sourcePath = Join-Path $source ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Required product documentation is missing: $relativePath"
        }

        $destinationPath = Join-Path $destination ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    }

    $expectedDocs = @($script:ProductDocumentationAllowlist | Where-Object { $_.StartsWith('docs/', [System.StringComparison]::Ordinal) } | ForEach-Object { $_.Substring(5) })
    $docsRoot = Join-Path $destination 'docs'
    $actualDocs = @()
    if (Test-Path -LiteralPath $docsRoot -PathType Container) {
        $docsPrefix = [System.IO.Path]::GetFullPath($docsRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $actualDocs = @(Get-ChildItem -LiteralPath $docsRoot -File -Recurse -Force | ForEach-Object { $_.FullName.Substring($docsPrefix.Length).Replace('\', '/') })
    }
    $unexpectedDocs = @($actualDocs | Where-Object { $_ -cnotin $expectedDocs })
    $missingDocs = @($expectedDocs | Where-Object { $_ -cnotin $actualDocs })
    if ($unexpectedDocs.Count -gt 0 -or $missingDocs.Count -gt 0 -or $actualDocs.Count -ne $expectedDocs.Count) {
        throw "Product documentation inventory differs from the allowlist; unexpected=[$($unexpectedDocs -join ', ')], missing=[$($missingDocs -join ', ')]."
    }
}

function Get-ProductPackageContentManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)

    $root = [System.IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Product directory does not exist: $root" }
    foreach ($requiredName in @('NetBootDhcpTool.exe', 'NetBootDhcpTool.Updater.exe', 'README_RUN.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $requiredName) -PathType Leaf)) {
            throw "Required product package file is missing: $requiredName"
        }
    }
    $prefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $manifest = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        if ($relative.Equals('PROJECT_MEMORY.md', [System.StringComparison]::OrdinalIgnoreCase) -or
            $relative -match '(?i)(^|/)(logs?|tests?|cache|caches|temp|tmp|staging|__pycache__|artifacts|release|bin|obj)(/|$)|\.(pdb|xml|log|tmp|bak|partial)$') {
            throw "Forbidden file in product package: $relative"
        }
        [pscustomobject]@{
            Path = $relative
            SourcePath = $_.FullName
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            Size = [long]$_.Length
        }
    } | Sort-Object Path)

    $duplicateRuntimeFiles = @($manifest | Where-Object { $_.Path -match '(?i)\.dll$' } | Group-Object { [System.IO.Path]::GetFileName($_.Path) } | Where-Object { $_.Count -gt 1 })
    if ($duplicateRuntimeFiles.Count -gt 0) {
        $duplicateNames = @($duplicateRuntimeFiles | ForEach-Object { $_.Name }) -join ', '
        throw "Duplicate loose runtime file names in product package: $duplicateNames"
    }

    $allowedRootDocumentation = @('README.md', 'README_RUN.txt')
    $unexpectedRootDocumentation = @($manifest | Where-Object {
        ($_.Path.IndexOf('/') -lt 0) -and
        ([System.IO.Path]::GetExtension($_.Path) -in @('.md', '.html', '.txt')) -and
        ($_.Path -notin $allowedRootDocumentation)
    })
    if ($unexpectedRootDocumentation.Count -gt 0) {
        throw "Unapproved root documentation in product package: $(@($unexpectedRootDocumentation | ForEach-Object { $_.Path }) -join ', ')"
    }

    $docsRoot = Join-Path $root 'docs'
    $expectedDocs = @($script:ProductDocumentationAllowlist | Where-Object { $_.StartsWith('docs/', [System.StringComparison]::Ordinal) } | ForEach-Object { $_.Substring(5) })
    $actualDocs = @()
    if (Test-Path -LiteralPath $docsRoot -PathType Container) {
        $docsPrefix = [System.IO.Path]::GetFullPath($docsRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $actualDocs = @($manifest | Where-Object { $_.Path.StartsWith('docs/', [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.Path.Substring(5) })
    }
    if (@($expectedDocs | Where-Object { $_ -cnotin $actualDocs }).Count -gt 0 -or @($actualDocs | Where-Object { $_ -cnotin $expectedDocs }).Count -gt 0) {
        throw 'Product documentation content manifest differs from the allowlist.'
    }

    return $manifest
}
