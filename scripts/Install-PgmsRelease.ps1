#requires -Version 7.0
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Leonardo Santos
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs/PGMS'),
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'This PGMS pre-release requires Windows x64.'
}
$version = '0.1.0-alpha.1'
$milestone = 'M1008'
$expectedSha256 = '1240B8E0E9545C5CA51AA39D5525543808714AB1E41525607C495AE40C955A6B'
$assetName = 'PGMS-0.1.0-alpha.1-win-x64.zip'
$downloadUri = "https://github.com/leoars/PostgresManagementStudio/releases/download/v$version/$assetName"
if ($expectedSha256 -cnotmatch '^[A-F0-9]{64}$') { throw 'Installer release checksum is missing or invalid.' }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char]92, [char]47)
$temporaryName = 'PGMS-Install-' + [Guid]::NewGuid().ToString('N')
$working = [IO.Path]::GetFullPath((Join-Path $tempRoot $temporaryName))
[IO.Directory]::CreateDirectory($working) | Out-Null
try {
    if ($ArchivePath) {
        $archive = (Get-Item -LiteralPath $ArchivePath).FullName
    } else {
        $archive = Join-Path $working $assetName
        Invoke-WebRequest -Uri $downloadUri -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $expectedSha256) {
        throw 'Archive SHA256 does not match this release. No installation was performed.'
    }
    $extracted = Join-Path $working 'extracted'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
    $roots = @(Get-ChildItem -LiteralPath $extracted -Force)
    if ($roots.Count -ne 1 -or !$roots[0].PSIsContainer -or $roots[0].Name -cne "$milestone-win-x64") {
        throw 'Unexpected release archive layout.'
    }
    $package = $roots[0].FullName
    . (Join-Path $package 'InstallationTools.ps1')
    $verified = Test-PgmsPackage $package
    if ($verified.Milestone -cne $milestone -or $verified.Mode -cne 'SelfContained') {
        throw 'Unexpected release package identity.'
    }
    $installed = Install-PgmsVersion $package $InstallRoot
    [pscustomobject]@{
        Version = $version
        Milestone = $milestone
        InstallRoot = $installed.InstallRoot
        Executable = (Join-Path $installed.PackagePath 'PGMS.exe')
        ArchiveSha256 = $expectedSha256
        ApplicationLaunched = $false
    }
} finally {
    # Remove only this invocation's random temporary directory, never the archive input or install root.
    $checked = [IO.Path]::GetFullPath($working)
    $insideTemp = $checked.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    $sameName = [IO.Path]::GetFileName($checked) -ceq $temporaryName
    if (!$insideTemp -or !$sameName) { throw 'Temporary cleanup target escaped its root.' }
    if (Test-Path -LiteralPath $checked) {
        if ((Get-Item -LiteralPath $checked -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Temporary cleanup target became a link; preserved for inspection.'
        }
        Remove-Item -LiteralPath $checked -Recurse -Force
    }
}
