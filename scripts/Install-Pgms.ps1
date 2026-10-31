#requires -Version 7.0
param([Parameter(Mandatory)][string]$PackagePath,[string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'Programs/PGMS'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InstallationTools.ps1')
Install-PgmsVersion $PackagePath $InstallRoot
