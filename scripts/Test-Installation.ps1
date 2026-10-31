#requires -Version 7.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InstallationTools.ps1')
$fixture=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/install-tests/'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture)|Out-Null
$count=0
function Check([bool]$Ok,[string]$Name){if(!$Ok){throw $Name}; $script:count++; Write-Host "PASS: $Name"}
function Refuse([scriptblock]$Action){try{& $Action|Out-Null;return $false}catch{return $true}}
function Package([string]$Milestone){
 $path=Join-Path $fixture $Milestone;[IO.Directory]::CreateDirectory($path)|Out-Null
 'fixture'|Set-Content (Join-Path $path 'PGMS.exe');'runtime fixture'|Set-Content (Join-Path $path 'coreclr.dll')
 New-PgmsPackageManifest $path $Milestone $true|Out-Null;return $path
}
$first=Package M1; $install=Join-Path $fixture 'installed é'
$result=Install-PgmsVersion $first $install
Check ($result.Milestone -eq 'M1' -and (Get-PgmsInstalledVersion $install M1)) 'Install copies and verifies version'
Check ((Get-Content (Join-Path $install 'current.json') -Raw|ConvertFrom-Json).Milestone -eq 'M1') 'Install activates copied version'
Check (Refuse {Install-PgmsVersion $first $install}) 'Existing installed version is preserved'
$foreign=Join-Path $fixture 'foreign';[IO.Directory]::CreateDirectory($foreign)|Out-Null;'personal'|Set-Content (Join-Path $foreign 'keep.txt')
Check (Refuse {Install-PgmsVersion $first $foreign}) 'Unmanaged nonempty destination refused'
Check (Refuse {Install-PgmsVersion $first (Join-Path $first 'nested')}) 'Source destination overlap refused'
'changed'|Set-Content (Join-Path $first 'PGMS.exe')
Check (Refuse {Install-PgmsVersion $first (Join-Path $fixture 'tampered')}) 'Tampered source refused before install'
Check (!(Test-Path (Join-Path $fixture 'tampered'))) 'Rejected source creates no installation root'
$second=Package M2
Install-PgmsVersion $second $install|Out-Null
Check ((Get-PgmsActiveVersion $install) -eq (Join-Path $install 'versions/M2')) 'Update activates second version'
Check (Test-Path (Join-Path $install 'versions/M1/PGMS.exe')) 'Update preserves previous version'
Set-PgmsActiveVersion $install M1|Out-Null
Check ((Get-PgmsActiveVersion $install) -eq (Join-Path $install 'versions/M1')) 'Rollback activates preserved version'
$changed=Join-Path $install 'versions/M2/PGMS.exe';$original=[IO.File]::ReadAllBytes($changed);'modified'|Set-Content $changed
Check (Refuse {Set-PgmsActiveVersion $install M2}) 'Tampered installed version cannot activate'
[IO.File]::WriteAllBytes($changed,$original)
$held=Enter-PgmsInstallLock $install
try {Check (Refuse {Set-PgmsActiveVersion $install M2}) 'Concurrent activation is refused under lock'} finally {$held.Dispose()}
$pointer=Join-Path $install 'current.json'; $pointerBytes=[IO.File]::ReadAllBytes($pointer)
foreach($invalid in @('{bad json','{"FormatVersion":"1","Milestone":"M1"}','{"FormatVersion":1,"Milestone":"../M1"}')) {
 [IO.File]::WriteAllText($pointer,$invalid)
 Check (Refuse {Set-PgmsActiveVersion $install M2}) 'Invalid pointer cannot be overwritten by activation'
 Check ((Refuse {Remove-PgmsInstalledVersion $install M1}) -and (Test-Path (Join-Path $install 'versions/M1'))) 'Invalid pointer cannot move installed version'
}
[IO.File]::WriteAllBytes($pointer,$pointerBytes)
Check ((Get-PgmsActiveVersion $install).EndsWith('M1')) 'Valid activation restored after invalid-metadata tests'
Move-Item -LiteralPath $pointer -Destination (Join-Path $install 'pointer-backup.json')
New-Item -ItemType Junction -Path $pointer -Target $foreign|Out-Null
Check (Refuse {Get-PgmsActiveVersion $install}) 'Linked activation pointer refused'
Check ((Refuse {Remove-PgmsInstalledVersion $install M1}) -and (Test-Path (Join-Path $install 'versions/M1'))) 'Linked pointer prevents version mutation'
Rename-Item -LiteralPath $pointer -NewName 'pointer-link-fixture'
Move-Item -LiteralPath (Join-Path $install 'pointer-backup.json') -Destination $pointer
Check ((Get-Content (Join-Path $foreign 'keep.txt') -Raw).Contains('personal')) 'Linked pointer target preserved'
$incomplete=Package M4
Remove-Item -LiteralPath (Join-Path $incomplete 'coreclr.dll')
Remove-Item -LiteralPath (Join-Path $incomplete 'package-manifest.json')
New-PgmsPackageManifest $incomplete M4 $true|Out-Null
Check (Refuse {Install-PgmsVersion $incomplete (Join-Path $fixture 'incomplete')}) 'Valid manifest without runtime cannot install'
Copy-Item -LiteralPath $incomplete -Destination (Join-Path $install 'versions/M4') -Recurse
Check (Refuse {Set-PgmsActiveVersion $install M4}) 'Installed package without runtime cannot activate'
$badRoot=Join-Path $fixture 'bad-marker';[IO.Directory]::CreateDirectory($badRoot)|Out-Null
'{"FormatVersion":"1","Product":"PGMS"}'|Set-Content (Join-Path $badRoot '.pgms-install.json')
Check (Refuse {Get-PgmsInstallRoot $badRoot}) 'Marker with coerced format type refused'
Check ((Get-PgmsActiveVersion $install).EndsWith('M1')) 'Rejected package and marker leave active version intact'
$pointerLock=[IO.File]::Open($pointer,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
 Check (Refuse {Set-PgmsActiveVersion $install M2}) 'Locked pointer refuses replacement'
 Check ((Read-PgmsCurrent $install).Milestone -eq 'M1') 'Failed pointer replacement preserves previous metadata'
 Check (@(Get-ChildItem -LiteralPath $install -Filter '.current.*.tmp' -Force).Count -eq 0) 'Failed pointer replacement cleans temporary file'
}finally{$pointerLock.Dispose()}
$pointerLock=[IO.File]::Open($pointer,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
 Check (Refuse {Remove-PgmsInstalledVersion $install M1}) 'Locked active pointer aborts uninstall'
 Check ((Get-PgmsInstalledVersion $install M1).EndsWith('M1') -and (Read-PgmsCurrent $install).Milestone -eq 'M1') 'Aborted uninstall restores directory and activation'
 Check (@(Get-ChildItem -LiteralPath (Join-Path $install 'versions') -Directory -Filter '.removed.*' -Force).Count -eq 0) 'Aborted uninstall leaves no orphan quarantine'
}finally{$pointerLock.Dispose()}
$residue=Join-Path $install 'versions/.M99.crash.partial';[IO.Directory]::CreateDirectory($residue)|Out-Null
'preserve'|Set-Content (Join-Path $residue 'keep.txt')
$inventory=Get-PgmsInstallationInfo $install
Check ($inventory.ActiveMilestone -eq 'M1' -and @($inventory.Versions|Where-Object {$_.Milestone -eq 'M1' -and $_.Valid}).Count -eq 1) 'Inventory identifies active verified version'
Check (@($inventory.Versions|Where-Object {$_.Milestone -eq 'M4' -and !$_.Valid}).Count -eq 1) 'Inventory reports incomplete installed version'
Check ($inventory.ResidualDirectories -contains '.M99.crash.partial' -and (Get-Content (Join-Path $residue 'keep.txt') -Raw).Contains('preserve')) 'Inventory reports residual without deleting content'
[IO.File]::WriteAllText($pointer,'bad json')
$inventory=Get-PgmsInstallationInfo $install
Check ($null -ne $inventory.ActivationError -and $inventory.Versions.Count -ge 2) 'Inventory retains version diagnostics with corrupt activation'
[IO.File]::WriteAllBytes($pointer,$pointerBytes)
$userData=Join-Path $fixture 'user-data.txt';'keep user data'|Set-Content $userData
Remove-PgmsInstalledVersion $install M2|Out-Null
Check (!(Test-Path (Join-Path $install 'versions/M2')) -and (Get-PgmsActiveVersion $install).EndsWith('M1')) 'Removing inactive version preserves active version'
Remove-PgmsInstalledVersion $install M1|Out-Null
Check (!(Test-Path (Join-Path $install 'current.json'))) 'Removing active version clears activation pointer'
Check ((Get-Content $userData -Raw).Contains('keep user data')) 'Removal preserves external user data'
Check (Refuse {Remove-PgmsInstalledVersion $install M999}) 'Unknown version removal refused'
Check ((Get-Content (Join-Path $foreign 'keep.txt') -Raw).Contains('personal')) 'Unmanaged personal file remains intact'
$third=Package M3;Install-PgmsVersion $third $install|Out-Null
'extra'|Set-Content (Join-Path $install 'versions/M3/unmanaged.txt')
Check (Refuse {Remove-PgmsInstalledVersion $install M3}) 'Extra files prevent recursive package removal'
$linked=Join-Path $fixture 'linked'
New-Item -ItemType Junction -Path $linked -Target $foreign|Out-Null
Check (Refuse {Get-PgmsInstallRoot $linked -Create}) 'Linked installation root refused'
$lifeFirst=Package M10;$lifeSecond=Package M11
$journey=& (Join-Path $PSScriptRoot 'Test-InstallLifecycle.ps1') -FirstPackage $lifeFirst -SecondPackage $lifeSecond -WorkRoot (Join-Path $fixture 'lifecycle')
Check ($journey.Updated -and $journey.RolledBack -and $journey.Reactivated -and $journey.Removed) 'Lifecycle verifies update rollback reactivation and removal'
Check ($journey.SyntheticDataPreserved -and !$journey.ApplicationLaunched -and !$journey.CleanWindowsCertified) 'Lifecycle distinguishes file checks from environment certification'
Check (Refuse {& (Join-Path $PSScriptRoot 'Test-InstallLifecycle.ps1') -FirstPackage $lifeFirst -SecondPackage $lifeFirst -WorkRoot (Join-Path $fixture 'same-lifecycle')}) 'Lifecycle refuses identical versions'
Write-Host "$count installation checks passed. Fixtures: $fixture"
