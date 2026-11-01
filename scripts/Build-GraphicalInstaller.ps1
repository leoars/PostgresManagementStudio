param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$CompilerPath,
    [string]$OutputDirectory,
    # CI builds the payload from the tagged source, so its manifest hash is not known before the build;
    # the switch skips only the frozen-hash comparison and keeps milestone and product version checks.
    [switch]$SkipFrozenManifest
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
$root = Split-Path $PSScriptRoot -Parent
$release = Get-Content (Join-Path $root 'installer/release.json') -Raw | ConvertFrom-Json
$verified = Test-PgmsPackage $PackagePath
$package = (Get-Item -LiteralPath $PackagePath).FullName
if ($verified.Mode -ne 'SelfContained' -or $verified.Milestone -cne $release.Milestone) { throw 'Wrong release package.' }
$manifestHash = (Get-FileHash (Join-Path $package 'package-manifest.json')).Hash
if (!$SkipFrozenManifest -and $manifestHash -cne $release.ManifestSha256) { throw 'Release manifest does not match the frozen payload.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'PGMS.dll')).ProductVersion -cne $release.Version) { throw 'Wrong product version.' }
$compiler = (Get-Item -LiteralPath $CompilerPath).FullName
if ((Get-AuthenticodeSignature -LiteralPath $compiler).Status -ne 'Valid') { throw 'Compiler signature is not valid.' }
$compilerVersion = (& $compiler --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '\b7\.1\.1\b') { throw 'Use the official Inno Setup 7.1.1 x64 compiler.' }
$license = Join-Path (Split-Path $compiler -Parent) 'license.txt'
if (!(Test-Path -LiteralPath $license)) { throw 'Compiler license is missing.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/graphical-installer/$($release.Version)" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output directory already exists; choose a new one.' }
New-Item -ItemType Directory -Path $output | Out-Null
$marker = Join-Path $output '.pgms-gui-installation.ini'
@"
[Installation]
Product=PGMS.GraphicalSetup.5A16E882-3D1B-4756-8A3E-A8DA775FC8C1
Version=$($release.Version)
Milestone=$($release.Milestone)
"@ | Set-Content -LiteralPath $marker -Encoding utf8NoBOM
& $compiler "/DPackageDir=$package" "/DOutputDir=$output" "/DMarkerFile=$marker" "/DCompilerLicense=$license" "/DReleaseVersion=$($release.Version)" "/DNumericVersion=$($release.NumericVersion)" "/DMilestone=$($release.Milestone)" (Join-Path $root 'installer/PGMS.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE. Failed output preserved." }
$exe = Join-Path $output "PGMS-$($release.Version)-Setup-x64.exe"
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($exe))" | Set-Content -LiteralPath "$exe.sha256" -Encoding ascii
[pscustomobject]@{ Installer = $exe; Sha256 = $hash; CompilerVersion = $compilerVersion; PayloadManifest = $manifestHash; FrozenManifestChecked = !$SkipFrozenManifest }
