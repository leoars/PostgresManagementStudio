#requires -Version 7.0
param([Parameter(Mandatory)][string]$Milestone,[string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'Programs/PGMS'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InstallationTools.ps1')
Set-PgmsActiveVersion $InstallRoot $Milestone
