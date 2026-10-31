$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
$testRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/package-tests/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path "$testRoot/nested" -Force | Out-Null
Set-Content -LiteralPath "$testRoot/PGMS.exe" -Value 'synthetic executable'
Set-Content -LiteralPath "$testRoot/nested/unicode.txt" -Value 'ação 😀'
$script:passed = 0
function Check-Rejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw "FAILED: $Name" }
    $script:passed++; Write-Host "PASS: $Name"
}
New-PgmsPackageManifest $testRoot 'M120' $true | Out-Null
$script:passed++; Write-Host 'PASS: valid nested Unicode fixture'
$original = Get-Content -LiteralPath "$testRoot/package-manifest.json" -Raw
Set-Content -LiteralPath "$testRoot/PGMS.exe" -Value 'synthetic executablE'
Check-Rejected { Test-PgmsPackage $testRoot } 'same-length tampering'
Set-Content -LiteralPath "$testRoot/PGMS.exe" -Value 'synthetic executable'
Rename-Item -LiteralPath "$testRoot/PGMS.exe" -NewName 'unexpected.exe'
Check-Rejected { Test-PgmsPackage $testRoot } 'missing and substituted file'
Rename-Item -LiteralPath "$testRoot/unexpected.exe" -NewName 'PGMS.exe'
Set-Content -LiteralPath "$testRoot/extra.txt" -Value 'extra'
Check-Rejected { Test-PgmsPackage $testRoot } 'extra file'
Remove-Item -LiteralPath "$testRoot/extra.txt"
foreach ($case in @('traversal', 'absolute', 'duplicate', 'hash', 'format', 'mode', 'negative', 'hash-array', 'json')) {
    $data = $original | ConvertFrom-Json
    switch ($case) {
        traversal { $data.Files[0].Path = '../outside' }
        absolute { $data.Files[0].Path = 'C:/outside' }
        duplicate { $data.Files += $data.Files[0] }
        'hash-array' { $data.Files[0].Sha256 = @($data.Files[0].Sha256) }
        hash { $data.Files[0].Sha256 = 'invalid' }
        format { $data.FormatVersion = 2 }
        mode { $data.Mode = 'Unknown' }
        negative { $data.Files[0].Length = -1 }
    }
    $json = $data | ConvertTo-Json -Depth 5
    if ($case -eq 'json') { $json = '{invalid' }
    Set-Content -LiteralPath "$testRoot/package-manifest.json" -Value $json
    Check-Rejected { Test-PgmsPackage $testRoot } "invalid manifest: $case"
}
Set-Content -LiteralPath "$testRoot/package-manifest.json" -Value $original
Test-PgmsPackage $testRoot | Out-Null
$script:passed++; Write-Host 'PASS: fixture restored'
Check-Rejected { New-PgmsPackageManifest $testRoot 'M120' $true } 'existing manifest preserved'
$archive = "$testRoot.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($testRoot, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
& (Join-Path $PSScriptRoot 'Test-PackageArchive.ps1') -ArchivePath $archive -DestinationPath "$testRoot-extracted" | Out-Null
$script:passed++; Write-Host 'PASS: archive roundtrip'
Check-Rejected { & (Join-Path $PSScriptRoot 'Test-PackageArchive.ps1') -ArchivePath $archive -DestinationPath "$testRoot-extracted" } 'existing extraction refused'
$corruptArchive = "$testRoot-corrupt.zip"
Copy-Item -LiteralPath $archive -Destination $corruptArchive
$zip = [IO.Compression.ZipFile]::Open($corruptArchive, [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.GetEntry([IO.Path]::GetFileName($testRoot) + '/PGMS.exe')
    $stream = $entry.Open(); $stream.SetLength(0)
    $writer = [IO.StreamWriter]::new($stream)
    try { $writer.Write('tampered') } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
Check-Rejected { & (Join-Path $PSScriptRoot 'Test-PackageArchive.ps1') -ArchivePath $corruptArchive -DestinationPath "$testRoot-corrupt" } 'tampered archive payload'
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.CreateEntry('extra.txt')
    $writer = [IO.StreamWriter]::new($entry.Open())
    try { $writer.Write('unexpected root file') } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
Check-Rejected { & (Join-Path $PSScriptRoot 'Test-PackageArchive.ps1') -ArchivePath $archive -DestinationPath "$testRoot-extra" } 'unexpected archive root'
$traversalArchive = "$testRoot-traversal.zip"
$zip = [IO.Compression.ZipFile]::Open($traversalArchive, [IO.Compression.ZipArchiveMode]::Create)
try { $null = $zip.CreateEntry('../outside-' + [IO.Path]::GetFileName($testRoot) + '.txt') } finally { $zip.Dispose() }
Check-Rejected { & (Join-Path $PSScriptRoot 'Test-PackageArchive.ps1') -ArchivePath $traversalArchive -DestinationPath "$testRoot-traversal" } 'archive traversal refused'
if (Test-Path -LiteralPath (Join-Path (Split-Path $testRoot -Parent) ('outside-' + [IO.Path]::GetFileName($testRoot) + '.txt'))) { throw 'Archive escaped destination.' }
$script:passed++; Write-Host 'PASS: no traversal output outside destination'
New-Item -ItemType Junction -Path "$testRoot/link" -Target "$testRoot/nested" | Out-Null
Check-Rejected { Test-PgmsPackage $testRoot } 'junction in package'
Write-Host "$script:passed package checks passed. Fixtures retained: $testRoot"
