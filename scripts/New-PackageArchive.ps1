param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
$package = (Get-Item -LiteralPath $PackagePath).FullName
$verified = Test-PgmsPackage $package
$name = "PGMS-$($verified.Milestone)"
if ($verified.Mode -eq 'SelfContained') { $name += '-win-x64' } else { $name += '-framework-dependent' }
$artifactRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'
$destination = Join-Path $artifactRoot "distribution/$name.zip"
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath "$destination.sha256")) { throw 'Archive/checksum already exists; overwrite refused.' }
New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
$staging = "$destination.$([Guid]::NewGuid().ToString('N')).partial"
[IO.Compression.ZipFile]::CreateFromDirectory($package, $staging, [IO.Compression.CompressionLevel]::Optimal, $true)
$extracted = Join-Path $artifactRoot "archive-checks/$([Guid]::NewGuid().ToString('N'))"
& (Join-Path $PSScriptRoot 'Test-PackageArchive.ps1') -ArchivePath $staging -DestinationPath $extracted | Out-Null
$hash = (Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash
[IO.File]::Move($staging, $destination)
$stream = [IO.File]::Open("$destination.sha256", [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
try { $writer.WriteLine("$hash  $([IO.Path]::GetFileName($destination))") } finally { $writer.Dispose() }
[pscustomobject]@{ Archive = $destination; Sha256 = $hash; VerifiedExtraction = $extracted }
