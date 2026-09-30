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
