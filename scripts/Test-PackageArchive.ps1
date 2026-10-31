param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$DestinationPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
$destination = [IO.Path]::GetFullPath($DestinationPath)
if (Test-Path -LiteralPath $destination) { throw 'Extraction destination must not exist.' }
# The framework rejects entries that escape the extraction directory and existing-file collisions.
[IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($ArchivePath), $destination)
$roots = @(Get-ChildItem -LiteralPath $destination -Force)
if ($roots.Count -ne 1 -or !$roots[0].PSIsContainer) { throw 'Archive must contain exactly one package directory.' }
$result = Test-PgmsPackage $roots[0].FullName
[pscustomobject]@{ PackagePath = $roots[0].FullName; Milestone = $result.Milestone; FileCount = $result.FileCount; Valid = $true }
