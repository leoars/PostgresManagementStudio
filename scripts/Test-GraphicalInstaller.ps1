param([Parameter(Mandatory)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$id = 'PGMS.GraphicalSetup.5A16E882-3D1B-4756-8A3E-A8DA775FC8C1_is1'
$key = "HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/$id"
if (Test-Path -LiteralPath $key) { throw 'An existing graphical installation must be preserved; use an isolated Windows account.' }
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Postgres Management Studio.lnk'
if (Test-Path -LiteralPath $desktop) { throw 'An existing desktop shortcut must be preserved; use an isolated Windows account.' }
$installer = (Get-Item -LiteralPath $InstallerPath).FullName
$root = Split-Path $PSScriptRoot -Parent
$trial = Join-Path $root "artifacts/graphical-tests/$([Guid]::NewGuid().ToString('N'))"
$target = Join-Path $trial 'installed'
$group = "PGMS installer test $([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $trial -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw "FAILED: $Name" }
    $checks.Add($Name)
}
function Run-Setup([string]$Destination, [string]$Name, [string]$Language = 'english') {
    $args = @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LANG=$Language", "/DIR=`"$Destination`"", "/GROUP=`"$group`"", "/LOG=`"$(Join-Path $trial "$Name.log")`"")
    $process = Start-Process -FilePath $installer -ArgumentList $args -WindowStyle Hidden -PassThru -Wait
    return $process.ExitCode
}
$sentinel = Join-Path $trial 'external-user-data.sql'
[IO.File]::WriteAllText($sentinel, '-- synthetic user data')
$unmanaged = Join-Path $trial 'unmanaged'
New-Item -ItemType Directory -Path $unmanaged | Out-Null
Copy-Item -LiteralPath $sentinel -Destination (Join-Path $unmanaged 'keep.sql')
Assert ((Run-Setup $unmanaged 'unmanaged') -ne 0) 'Refuses unmanaged nonempty target'
Assert ((Get-Content (Join-Path $unmanaged 'keep.sql') -Raw) -ceq '-- synthetic user data') 'Preserves unmanaged content'
$linked = Join-Path $trial 'linked'
New-Item -ItemType Junction -Path $linked -Target $unmanaged | Out-Null
Assert ((Run-Setup $linked 'junction') -ne 0) 'Refuses linked target'
Assert ((Run-Setup $target 'install') -eq 0) 'Current-user installation succeeds'
Assert (Test-Path -LiteralPath $key) 'Registers native uninstaller'
$payload = Join-Path $target 'versions/M1008'
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath $payload | Out-Null
Assert $true 'Installed payload matches complete frozen manifest'
$shell = New-Object -ComObject WScript.Shell
Assert ((Test-Path -LiteralPath $desktop) -and ($shell.CreateShortcut($desktop).TargetPath -ieq (Join-Path $payload 'PGMS.exe'))) 'Creates desktop shortcut by default with correct target'
& (Join-Path $PSScriptRoot 'Test-PackageLaunch.ps1') -PackagePath $payload | Out-Null
Assert $true 'Installed app opens with bundled runtime and closes normally'
$extra = Join-Path $target 'keep-user-query.sql'
Copy-Item -LiteralPath $sentinel -Destination $extra
$lock = [IO.File]::Open((Join-Path $payload 'PGMS.exe'), 'Open', 'Read', 'None')
try { Assert ((Run-Setup $target 'busy') -ne 0) 'Refuses repair of locked executable' } finally { $lock.Dispose() }
Assert ((Run-Setup $target 'repair' 'brazilianportuguese') -eq 0) 'Same-version repair in Portuguese succeeds'
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath $payload | Out-Null
Assert $true 'Repair preserves exact payload'
$uninstaller = Join-Path $target 'unins000.exe'
$process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $trial 'uninstall.log')`"") -WindowStyle Hidden -PassThru -Wait
Assert ($process.ExitCode -eq 0) 'Native uninstall succeeds'
Assert (!(Test-Path -LiteralPath $key)) 'Removes native uninstaller registration'
Assert (!(Test-Path -LiteralPath $desktop)) 'Removes owned desktop shortcut'
Assert (!(Test-Path -LiteralPath $payload)) 'Removes owned package files'
Assert ((Get-Content $extra -Raw) -ceq '-- synthetic user data') 'Preserves additional user SQL inside installation root'
Assert ((Get-Content $sentinel -Raw) -ceq '-- synthetic user data') 'Preserves external user state'
$report = [pscustomobject]@{ Valid = $true; InstallerSha256 = (Get-FileHash $installer).Hash; Scope = 'CurrentUser'; GlobalInstallExecuted = $false; Checks = $checks.ToArray(); Trial = $trial }
$report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $trial 'result.json') -Encoding utf8NoBOM
$report
