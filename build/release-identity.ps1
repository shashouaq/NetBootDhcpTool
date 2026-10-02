function Get-NetBootVersionMetadata {
    param([string]$Version, [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
    [xml]$source = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/Version.props')
    if (-not $Version) { $Version = [string]$source.Project.PropertyGroup.Version.'#text' }
    if ($Version -cnotmatch '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw 'Invalid SemVer product version.'
    }
    $numeric = @($Matches.major, $Matches.minor, $Matches.patch)
    foreach ($part in $numeric) { if ([decimal]$part -gt 65534) { throw 'PE version components must be in 0..65534.' } }
    [pscustomobject]@{
        Version = $Version; NumericVersion = (($numeric -join '.') + '.0')
        ProductName = [string]$source.Project.PropertyGroup.NetBootProductName
        CompanyName = [string]$source.Project.PropertyGroup.NetBootCompanyName
        Copyright = [string]$source.Project.PropertyGroup.NetBootCopyright
    }
}

function Get-NetBootReleaseIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Tag, [switch]$ReleaseCandidate)
    if ($ReleaseCandidate) {
        if ($Tag -cnotmatch '^v(?<version>\d+\.\d+\.\d+)-rc\.[1-9]\d*$') {
            throw 'ReleaseCandidate requires an explicit v<version>-rc.<positive number> tag; stable tags are forbidden.'
        }
    } elseif ($Tag -cnotmatch '^v(?<version>\d+\.\d+\.\d+)$') {
        throw 'Formal release requires v<version>; an RC must explicitly select ReleaseCandidate mode.'
    }
    [pscustomobject]@{ Tag=$Tag; Version=$Matches.version; IsCandidate=[bool]$ReleaseCandidate }
}
