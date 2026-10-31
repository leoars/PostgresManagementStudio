#requires -Version 7.0
param([string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'Programs/PGMS'),[switch]$AsJson)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InstallationTools.ps1')
$report=Get-PgmsInstallationInfo $InstallRoot
if($AsJson){$report|ConvertTo-Json -Depth 6}else{$report}
