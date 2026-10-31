#requires -Version 7.0
param([Parameter(Mandatory)][string]$FirstPackage,[Parameter(Mandatory)][string]$SecondPackage,[Parameter(Mandatory)][string]$WorkRoot)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InstallationTools.ps1')
$first=Test-PgmsPackage $FirstPackage;$second=Test-PgmsPackage $SecondPackage
if($first.Milestone -ceq $second.Milestone){throw 'Lifecycle requires two different versions.'}
$work=Assert-PgmsRegularPath $WorkRoot
if(Test-Path -LiteralPath $work){throw 'Lifecycle requires a new isolated work root.'}
[IO.Directory]::CreateDirectory($work)|Out-Null
$install=Join-Path $work 'installed';$data=Join-Path $work 'user-data.txt'
[IO.File]::WriteAllText($data,'synthetic preserved data')
Install-PgmsVersion $FirstPackage $install|Out-Null
Install-PgmsVersion $SecondPackage $install|Out-Null
if(!(Get-PgmsActiveVersion $install).EndsWith([IO.Path]::DirectorySeparatorChar+$second.Milestone)){throw 'Update did not activate second version.'}
Set-PgmsActiveVersion $install $first.Milestone|Out-Null
if(!(Get-PgmsActiveVersion $install).EndsWith([IO.Path]::DirectorySeparatorChar+$first.Milestone)){throw 'Rollback did not activate first version.'}
Set-PgmsActiveVersion $install $second.Milestone|Out-Null
Remove-PgmsInstalledVersion $install $first.Milestone|Out-Null
Remove-PgmsInstalledVersion $install $second.Milestone|Out-Null
if((Test-Path (Join-Path $install 'current.json')) -or @(Get-ChildItem -LiteralPath (Join-Path $install 'versions') -Directory -Force).Count){throw 'Lifecycle removal left an installed version.'}
if([IO.File]::ReadAllText($data) -cne 'synthetic preserved data'){throw 'Lifecycle changed synthetic user data.'}
$report=[pscustomobject]@{First=$first.Milestone;Second=$second.Milestone;WorkRoot=$work;Updated=$true;RolledBack=$true;Reactivated=$true;Removed=$true;SyntheticDataPreserved=$true;ApplicationLaunched=$false;CleanWindowsCertified=$false}
$report|ConvertTo-Json|Set-Content (Join-Path $work 'lifecycle-result.json') -Encoding utf8
$report
