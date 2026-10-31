Set-StrictMode -Version Latest

function Get-PgmsPackageFiles([string]$PackagePath) {
    $root = Get-Item -LiteralPath $PackagePath -Force
    if (!$root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Package must be a regular directory.' }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root.FullName)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links are not allowed in packages: $($item.Name)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            $relative = [IO.Path]::GetRelativePath($root.FullName, $item.FullName).Replace('\', '/')
            if ($relative -ieq 'package-manifest.json') { continue }
            [pscustomobject]@{ Path = $relative; Length = $item.Length; Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
        }
    }
}

function New-PgmsPackageManifest([string]$PackagePath, [string]$Milestone, [bool]$SelfContained) {
    if ($Milestone -cnotmatch '^M[1-9][0-9]*$') { throw 'Invalid milestone.' }
    $manifestPath = Join-Path $PackagePath 'package-manifest.json'
    if (Test-Path -LiteralPath $manifestPath) { throw 'Manifest already exists.' }
    $files = @(Get-PgmsPackageFiles $PackagePath | Sort-Object Path)
    if (!$files.Count) { throw 'Package is empty.' }
    $mode = if ($SelfContained) { 'SelfContained' } else { 'FrameworkDependent' }
    [ordered]@{ FormatVersion = 1; Milestone = $Milestone; Mode = $mode; Files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    Test-PgmsPackage $PackagePath
}

function Test-PgmsPackage([string]$PackagePath) {
    # Inventory first: never follow paths supplied by a manifest, including links.
    $actual = @(Get-PgmsPackageFiles $PackagePath)
    $manifest = Get-Content -LiteralPath (Join-Path $PackagePath 'package-manifest.json') -Raw | ConvertFrom-Json
    if (($manifest.FormatVersion -isnot [long] -and $manifest.FormatVersion -isnot [int]) -or $manifest.Milestone -isnot [string] -or $manifest.Mode -isnot [string] -or $manifest.FormatVersion -ne 1 -or $manifest.Milestone -cnotmatch '^M[1-9][0-9]*$' -or $manifest.Mode -cnotin @('SelfContained', 'FrameworkDependent')) { throw 'Unsupported package metadata.' }
    if ($manifest.Files -isnot [array] -or !$manifest.Files.Count) { throw 'Manifest files must be a nonempty array.' }
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.Files) {
        if ($file.Path -isnot [string] -or !$file.Path -or $file.Path -match '[\\:<>"|?*\x00-\x1f]' -or $file.Path -match '(^|/)(\.{1,2}|)(/|$)' -or $file.Path -match '[. ](/|$)' -or $file.Path -ieq 'package-manifest.json') { throw 'Invalid manifest path.' }
        if ($file.Sha256 -isnot [string] -or $file.Sha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or $file.Length -isnot [long] -and $file.Length -isnot [int] -or $file.Length -lt 0) { throw 'Invalid file metadata.' }
        if (!$expected.TryAdd($file.Path, $file)) { throw 'Duplicate manifest path.' }
    }
    if ($actual.Count -ne $expected.Count) { throw 'Package file count differs from manifest (missing or extra files).' }
    foreach ($file in $actual) {
        if (!$expected.ContainsKey($file.Path)) { throw "Unexpected package file: $($file.Path)" }
        $entry = $expected[$file.Path]
        if ($entry.Length -ne $file.Length -or $entry.Sha256 -ine $file.Sha256) { throw "Package file changed: $($file.Path)" }
    }
    [pscustomobject]@{ Milestone = $manifest.Milestone; Mode = $manifest.Mode; FileCount = $actual.Count; Valid = $true }
}
