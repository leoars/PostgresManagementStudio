#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$Milestone,[string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'Programs/PGMS'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InstallationTools.ps1')
if($PSCmdlet.ShouldProcess("$InstallRoot/$Milestone",'Remove installed PGMS version')){Remove-PgmsInstalledVersion $InstallRoot $Milestone}
