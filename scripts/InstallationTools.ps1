#requires -Version 7.0
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
function Assert-PgmsRegularPath([string]$Path) {
 $full=[IO.Path]::GetFullPath($Path)
 if($full.TrimEnd('\','/') -eq [IO.Path]::GetPathRoot($full).TrimEnd('\','/')){throw 'Installation root cannot be a filesystem root.'}
 $part=$full
 while($part){
  if(Test-Path -LiteralPath $part){if((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Installation paths cannot contain links.'}}
  $parent=Split-Path $part -Parent
  if($parent -eq $part){break}; $part=$parent
 }
 return $full.TrimEnd('\','/')
}
function Get-PgmsInstallRoot([string]$Path,[switch]$Create) {
 $root=Assert-PgmsRegularPath $Path
 $marker=Join-Path $root '.pgms-install.json'
 if(!(Test-Path -LiteralPath $marker)){
  if(!$Create){throw 'Not a managed PGMS installation.'}
  if((Test-Path -LiteralPath $root) -and @(Get-ChildItem -LiteralPath $root -Force).Count){throw 'Refusing nonempty unmanaged installation root.'}
  [IO.Directory]::CreateDirectory($root)|Out-Null
  $stream=[IO.File]::Open($marker,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try{$bytes=[Text.Encoding]::UTF8.GetBytes('{"FormatVersion":1,"Product":"PGMS"}');$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
 }
 Assert-PgmsRegularPath $marker|Out-Null
 $data=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
 if(($data.FormatVersion -isnot [long] -and $data.FormatVersion -isnot [int]) -or $data.FormatVersion -ne 1 -or $data.Product -isnot [string] -or $data.Product -cne 'PGMS'){throw 'Invalid installation marker.'}
 return $root
}
function Enter-PgmsInstallLock([string]$Root) {
 $path=Join-Path $Root '.install.lock'; Assert-PgmsRegularPath $path|Out-Null
 return [IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}
function Read-PgmsCurrent([string]$Root) {
 $pointer=Join-Path $Root 'current.json'; Assert-PgmsRegularPath $pointer|Out-Null
 if(!(Test-Path -LiteralPath $pointer)){return $null}
 $data=Get-Content -LiteralPath $pointer -Raw|ConvertFrom-Json
 if($data.FormatVersion -isnot [long] -and $data.FormatVersion -isnot [int]){throw 'Invalid activation format type.'}
 if($data.FormatVersion -ne 1 -or $data.Milestone -isnot [string] -or $data.Milestone -cnotmatch '^M[1-9][0-9]*$'){throw 'Invalid activation metadata.'}
 return $data
}
function Write-PgmsCurrent([string]$Root,[string]$Milestone) {
 if($Milestone -cnotmatch '^M[1-9][0-9]*$'){throw 'Invalid installed milestone.'}
 $destination=Join-Path $Root 'current.json'; Assert-PgmsRegularPath $destination|Out-Null
 $temporary=Join-Path $Root ('.current.'+[Guid]::NewGuid().ToString('N')+'.tmp')
 try {
  [IO.File]::WriteAllText($temporary,(@{FormatVersion=1;Milestone=$Milestone}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
  [IO.File]::Move($temporary,$destination,$true)
 } finally {if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
}
function Get-PgmsInstalledVersion([string]$Root,[string]$Milestone) {
 if($Milestone -cnotmatch '^M[1-9][0-9]*$'){throw 'Invalid installed milestone.'}
 $path=Assert-PgmsRegularPath (Join-Path $Root "versions/$Milestone")
 $verified=Test-PgmsPackage $path
 if($verified.Milestone -cne $Milestone -or $verified.Mode -cne 'SelfContained'){throw 'Installed version does not match self-contained package metadata.'}
 foreach($required in @('PGMS.exe','coreclr.dll')){if(!(Test-Path -LiteralPath (Join-Path $path $required) -PathType Leaf)){throw "Required installed file missing: $required"}}
 return $path
}
function Install-PgmsVersion([string]$PackagePath,[string]$InstallRoot) {
 $source=(Get-Item -LiteralPath $PackagePath).FullName.TrimEnd('\','/')
 $proposed=Assert-PgmsRegularPath $InstallRoot
 if($source -eq $proposed -or $proposed.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($proposed+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Package and installation paths must not overlap.'}
 $manifestHash=(Get-FileHash -LiteralPath (Join-Path $PackagePath 'package-manifest.json') -Algorithm SHA256).Hash
 $verified=Test-PgmsPackage $PackagePath
 if((Get-FileHash -LiteralPath (Join-Path $PackagePath 'package-manifest.json') -Algorithm SHA256).Hash -cne $manifestHash){throw 'Source manifest changed during verification.'}
 if($verified.Mode -cne 'SelfContained'){throw 'Installer requires self-contained package.'}
 foreach($required in @('PGMS.exe','coreclr.dll')){if(!(Test-Path -LiteralPath (Join-Path $PackagePath $required) -PathType Leaf)){throw "Required package file missing: $required"}}
 $root=Get-PgmsInstallRoot $InstallRoot -Create
 $lock=Enter-PgmsInstallLock $root
 try{
  Read-PgmsCurrent $root|Out-Null
  $versions=Assert-PgmsRegularPath (Join-Path $root 'versions'); [IO.Directory]::CreateDirectory($versions)|Out-Null
  $destination=Join-Path $versions $verified.Milestone
  if(Test-Path -LiteralPath $destination){throw 'Installed version already exists; preserve it or activate it explicitly.'}
  $staging=Join-Path $versions ('.'+$verified.Milestone+'.'+[Guid]::NewGuid().ToString('N')+'.partial')
  [IO.Directory]::CreateDirectory($staging)|Out-Null
  foreach($file in Get-ChildItem -LiteralPath $PackagePath -Force){Copy-Item -LiteralPath $file.FullName -Destination $staging -Recurse}
  $copied=Test-PgmsPackage $staging
  if($copied.Milestone -cne $verified.Milestone -or $copied.Mode -cne $verified.Mode -or (Get-FileHash -LiteralPath (Join-Path $staging 'package-manifest.json') -Algorithm SHA256).Hash -cne $manifestHash){throw 'Copied package differs from verified source.'}
  [IO.Directory]::Move($staging,$destination)
  Write-PgmsCurrent $root $verified.Milestone
  [pscustomobject]@{InstallRoot=$root;Milestone=$verified.Milestone;PackagePath=$destination}
 }finally{$lock.Dispose()}
}
function Set-PgmsActiveVersion([string]$InstallRoot,[string]$Milestone) {
 $root=Get-PgmsInstallRoot $InstallRoot; $lock=Enter-PgmsInstallLock $root
 try{Read-PgmsCurrent $root|Out-Null; $path=Get-PgmsInstalledVersion $root $Milestone; Write-PgmsCurrent $root $Milestone; return $path}finally{$lock.Dispose()}
}
function Get-PgmsActiveVersion([string]$InstallRoot) {
 $root=Get-PgmsInstallRoot $InstallRoot; $lock=Enter-PgmsInstallLock $root
 try{
  $data=Read-PgmsCurrent $root
  if($null -eq $data){throw 'No active installed version.'}
  return Get-PgmsInstalledVersion $root $data.Milestone
 }finally{$lock.Dispose()}
}
function Remove-PgmsInstalledVersion([string]$InstallRoot,[string]$Milestone) {
 $root=Get-PgmsInstallRoot $InstallRoot; $lock=Enter-PgmsInstallLock $root
 try{
  $current=Read-PgmsCurrent $root
  $version=Get-PgmsInstalledVersion $root $Milestone
  $versions=Assert-PgmsRegularPath (Join-Path $root 'versions')
  if(!$version.StartsWith($versions+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Version outside installation root.'}
  $quarantine=Join-Path $versions ('.removed.'+[Guid]::NewGuid().ToString('N'))
  Assert-PgmsRegularPath $quarantine|Out-Null
  if(!$quarantine.StartsWith($versions+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Quarantine outside versions.'}
  [IO.Directory]::Move($version,$quarantine)
  $pointer=Join-Path $root 'current.json'; Assert-PgmsRegularPath $pointer|Out-Null
  try {
   if($null -ne $current -and $current.Milestone -ceq $Milestone){Remove-Item -LiteralPath $pointer}
  } catch {
   # Both absolute paths were checked under versions before moving. Restore availability.
   [IO.Directory]::Move($quarantine,$version)
   throw
  }
  $checked=Assert-PgmsRegularPath $quarantine
  if(!$checked.StartsWith($versions+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Removal target outside versions.'}
  # Inventory validation above excludes links and extra files. Remove only quarantined package.
  Remove-Item -LiteralPath $checked -Recurse
  [pscustomobject]@{Removed=$Milestone;InstallRoot=$root;UserDataPreserved=$true}
 }finally{$lock.Dispose()}
}
function Get-PgmsInstallationInfo([string]$InstallRoot) {
 $root=Get-PgmsInstallRoot $InstallRoot; $lock=Enter-PgmsInstallLock $root
 try {
  $active=$null;$activationError=$null
  try{$current=Read-PgmsCurrent $root;if($null -ne $current){$active=$current.Milestone}}catch{$activationError=$_.Exception.Message}
  $versions=@();$residues=@();$directory=Assert-PgmsRegularPath (Join-Path $root 'versions')
  if(Test-Path -LiteralPath $directory){foreach($item in Get-ChildItem -LiteralPath $directory -Directory -Force){
   if($item.Name -cmatch '^M[1-9][0-9]*$'){
    try{Get-PgmsInstalledVersion $root $item.Name|Out-Null;$versions+= [pscustomobject]@{Milestone=$item.Name;Valid=$true;Error=$null}}
    catch{$versions+= [pscustomobject]@{Milestone=$item.Name;Valid=$false;Error=$_.Exception.Message}}
   }else{$residues+=$item.Name}
  }}
  [pscustomobject]@{InstallRoot=$root;ActiveMilestone=$active;ActivationError=$activationError;Versions=@($versions);ResidualDirectories=@($residues)}
 }finally{$lock.Dispose()}
}
