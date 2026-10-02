$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-identity.ps1')
$metadata = Get-NetBootVersionMetadata -RepositoryRoot $repoRoot
$version = $metadata.Version
[xml]$app = Get-Content (Join-Path $repoRoot 'src/NetBootDhcpTool.App/NetBootDhcpTool.App.csproj')
if ($app.Project.PropertyGroup.Version -or $app.Project.PropertyGroup.AssemblyVersion -or $app.Project.PropertyGroup.FileVersion) { throw 'App must inherit version metadata from the single version source.' }

$readme = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'README.md')
$maintenance = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'docs\MAINTENANCE_GUIDE.md')
$changelog = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'docs\FEATURE_CHANGELOG.md')
if ($readme -notmatch "(?m)^Version:\s*$([regex]::Escape($version))\s*$") { throw "README version does not match app version $version." }
$maintenanceVersionLine = "- Current application version: ``$version``"
if ($maintenance -notmatch [regex]::Escape($maintenanceVersionLine)) { throw "Maintenance guide version does not match app version $version." }
if ($changelog -notmatch '(?m)\A# Feature Change Log\r?\n\r?\n##\s+Unreleased\s*$') { throw 'Feature change log must keep an Unreleased section at the top.' }
$releaseSection = [regex]::Match($changelog, "(?ms)^##\s+v$([regex]::Escape($version))\b.*?(?=^##\s+|\z)")
if (-not $releaseSection.Success) { throw "Feature change log is missing the current release section v$version." }
foreach ($requiredField in @('Type:', 'Affected files/modules:', 'Concrete change:', 'Verification:', 'User impact:')) {
    if ($releaseSection.Value -notmatch "(?m)^-\s+$([regex]::Escape($requiredField))") { throw "Current change log section is missing '$requiredField'." }
}

$targetFrameworks = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter '*.csproj' -Recurse | ForEach-Object {
    [xml]$xml = Get-Content -LiteralPath $_.FullName
    [string]$xml.Project.PropertyGroup.TargetFramework
}
if ($targetFrameworks.Count -eq 0 -or @($targetFrameworks | Where-Object { $_ -notmatch '^net10\.0(?:-windows)?$' }).Count -gt 0) {
    throw 'All source projects must target .NET 10.'
}

$defaultsPath = Join-Path $repoRoot 'src\NetBootDhcpTool.Core\Defaults.cs'
$defaultSource = Get-Content -Raw -LiteralPath $defaultsPath
$defaultKeys = @([regex]::Matches($defaultSource, '(?m)^\s*\["([^"]+)"\]\s*=') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$locales = @('zh-CN', 'en-US')
foreach ($locale in $locales) {
    $localePath = Join-Path $repoRoot "i18n\$locale.json"
    $localeObject = Get-Content -Raw -LiteralPath $localePath | ConvertFrom-Json
    $localeKeys = @($localeObject.PSObject.Properties.Name | Sort-Object -Unique)
    $missing = @($defaultKeys | Where-Object { $_ -notin $localeKeys })
    $extra = @($localeKeys | Where-Object { $_ -notin $defaultKeys })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        throw "$locale localization mismatch. Missing: $($missing -join ', '); extra: $($extra -join ', ')"
    }
}

$mainXaml = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'src\NetBootDhcpTool.App\MainWindow.xaml')
$buttonTags = [regex]::Matches($mainXaml, '<Button\b[^>]*>').Count
$helpButtons = [regex]::Matches($mainXaml, '<Button\b[^>]*HelpButtonService\.HelpKey=').Count
if ($buttonTags -ne $helpButtons) { throw "Static main-window help coverage is $helpButtons/$buttonTags buttons." }

Write-Output "MAINTENANCE_CHECK_OK version=$version target=net10.0 localizationKeys=$($defaultKeys.Count) staticButtons=$helpButtons/$buttonTags"
