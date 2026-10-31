#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [string]$PreviousPackage
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'InstallationTools.ps1')
$project = Split-Path $PSScriptRoot -Parent
$work = Join-Path $project ('artifacts/release-installer-checks/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
$archive = (Get-Item -LiteralPath $ArchivePath).FullName
$installer = Join-Path $PSScriptRoot 'Install-PgmsRelease.ps1'
$install = Join-Path $work 'installed'
$sentinel = Join-Path $work 'synthetic-state.txt'
[IO.File]::WriteAllText($sentinel, 'synthetic user data preserved')
$passed = 0
function Check([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw "FAILED: $Name" }
    $script:passed++
    Write-Host "PASS: $Name"
}
function Rejected([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch { return $_.Exception.Message -match $Expected }
    return $false
}
$corrupt = Join-Path $work 'corrupt.zip'
Copy-Item -LiteralPath $archive -Destination $corrupt
$stream = [IO.File]::Open($corrupt, [IO.FileMode]::Open, [IO.FileAccess]::Write)
try { $stream.WriteByte(0) } finally { $stream.Dispose() }
Check (Rejected { & $installer -ArchivePath $corrupt -InstallRoot $install } 'SHA256') 'Changed archive rejected before installation'
Check (!(Test-Path -LiteralPath $install)) 'Rejected checksum creates no installation root'
$unmanaged = Join-Path $work 'unmanaged'
[IO.Directory]::CreateDirectory($unmanaged) | Out-Null
[IO.File]::WriteAllText((Join-Path $unmanaged 'keep.txt'), 'preserve me')
Check (Rejected { & $installer -ArchivePath $archive -InstallRoot $unmanaged } 'unmanaged') 'Unmanaged nonempty target refused'
Check ([IO.File]::ReadAllText((Join-Path $unmanaged 'keep.txt')) -ceq 'preserve me') 'Unmanaged target data preserved'
$launches = @()
$previous = $null
if ($PreviousPackage) {
    $previous = Test-PgmsPackage $PreviousPackage
    if ($previous.Milestone -ceq 'M1008') { throw 'Previous package must be a different version.' }
    Install-PgmsVersion $PreviousPackage $install | Out-Null
    $launches += & (Join-Path $PSScriptRoot 'Test-PackageLaunch.ps1') -PackagePath (Get-PgmsActiveVersion $install)
}
$result = & $installer -ArchivePath $archive -InstallRoot $install
Check ($result.Milestone -ceq 'M1008' -and !$result.ApplicationLaunched) 'Pinned release installed without launching or connecting'
$active = Get-PgmsActiveVersion $install
Check ((Test-PgmsPackage $active).Milestone -ceq 'M1008') 'Release is the active managed version'
Check ((Get-FileHash (Join-Path $active 'LICENSE')).Hash -ceq (Get-FileHash (Join-Path $project 'LICENSE')).Hash) 'Installed project license matches source'
$launches += & (Join-Path $PSScriptRoot 'Test-PackageLaunch.ps1') -PackagePath $active
$pointerHash = (Get-FileHash (Join-Path $install 'current.json')).Hash
Check (Rejected { & $installer -ArchivePath $archive -InstallRoot $install } 'already exists') 'Duplicate install refused without overwrite'
Check ((Get-FileHash (Join-Path $install 'current.json')).Hash -ceq $pointerHash) 'Duplicate attempt preserves activation pointer'
if ($previous) {
    Set-PgmsActiveVersion $install $previous.Milestone | Out-Null
    $launches += & (Join-Path $PSScriptRoot 'Test-PackageLaunch.ps1') -PackagePath (Get-PgmsActiveVersion $install)
    Set-PgmsActiveVersion $install 'M1008' | Out-Null
    $launches += & (Join-Path $PSScriptRoot 'Test-PackageLaunch.ps1') -PackagePath (Get-PgmsActiveVersion $install)
    Remove-PgmsInstalledVersion $install $previous.Milestone | Out-Null
}
Check (@($launches | Where-Object { !$_.Valid -or $_.ExitCode -ne 0 }).Count -eq 0) 'All installed launches use bundled runtime and exit normally'
Remove-PgmsInstalledVersion $install 'M1008' | Out-Null
$final = Get-PgmsInstallationInfo $install
Check ($null -eq $final.ActiveMilestone -and $final.Versions.Count -eq 0 -and $final.ResidualDirectories.Count -eq 0) 'Managed versions removed without residues'
Check ([IO.File]::ReadAllText($sentinel) -ceq 'synthetic user data preserved') 'External synthetic user data preserved'
[pscustomobject]@{ Valid=$true; Checks=$passed; Fixture=$work; Launches=$launches; RollbackTested=($null -ne $previous); Removed=$true; SyntheticOnly=$true }
