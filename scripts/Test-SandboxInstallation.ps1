#requires -Version 7.0
param([string]$PackagePath='C:\PGMS',[string]$ResultPath=(Join-Path $env:USERPROFILE 'Desktop/PGMS-install-result.json'))
$ErrorActionPreference='Stop'
. (Join-Path $PackagePath 'InstallationTools.ps1')
$root=Join-Path $env:LOCALAPPDATA 'Programs/PGMS-acceptance'
$result=Install-PgmsVersion $PackagePath $root
$active=Get-PgmsActiveVersion $root
$verified=Test-PgmsPackage $active
[ordered]@{Installed=$result;Verified=$verified;Windows=[Environment]::OSVersion.VersionString;PowerShell=$PSVersionTable.PSVersion.ToString();ApplicationLaunchTested=$false;CleanEnvironmentCertified=$false} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding utf8
Write-Host "Installation checked. Run application and manual acceptance before certifying environment. Result: $ResultPath"
