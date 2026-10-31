param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
$package = (Get-Item -LiteralPath $PackagePath).FullName
$verified = Test-PgmsPackage $package
$buildInfo = Get-Content -LiteralPath (Join-Path $projectRoot 'src/PGMS.Desktop/BuildInfo.cs') -Raw
if ($buildInfo -notmatch 'Milestone = "(M[0-9]+)"' -or $verified.Milestone -cne $Matches[1]) {
    throw 'Guide comparison requires the current workspace milestone; historical packages are preserved.'
}
$guides = foreach ($item in @(
    @{ Source='pt-BR/distribution.md'; Destination='LEIA-ME.md' },
    @{ Source='en/distribution.md'; Destination='READ-ME.md' }
)) {
    $source = Join-Path $projectRoot ('docs/' + $item.Source)
    $destination = Join-Path $package $item.Destination
    if (!(Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Package guide missing: $($item.Destination)" }
    $expected = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($actual -cne $expected) { throw "Package guide differs from workspace: $($item.Destination)" }
    [pscustomobject]@{ File=$item.Destination; Sha256=$actual; MatchesSource=$true }
}
[pscustomobject]@{ Milestone=$verified.Milestone; Valid=$true; Guides=@($guides); Limitation='Compares the current workspace guides; not a historical documentation validator or clean Windows certification.' }
