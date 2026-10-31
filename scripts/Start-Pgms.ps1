#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param([string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'Programs/PGMS'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InstallationTools.ps1')
$version=Get-PgmsActiveVersion $InstallRoot
if($PSCmdlet.ShouldProcess($version,'Launch PGMS')){Start-Process -FilePath (Join-Path $version 'PGMS.exe') -WorkingDirectory $version -WindowStyle Normal -PassThru}
