#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$english = @(Get-ChildItem -LiteralPath (Join-Path $root 'docs/en') -File -Filter '*.md' | Sort-Object Name)
$portuguese = @(Get-ChildItem -LiteralPath (Join-Path $root 'docs/pt-BR') -File -Filter '*.md' | Sort-Object Name)
if (@(Compare-Object $english.Name $portuguese.Name).Count) { throw 'Public guide language sets differ.' }
$pairs = @(
    @('README.md', 'README.pt-BR.md'),
    @('CONTRIBUTING.md', 'CONTRIBUTING.pt-BR.md'),
    @('SECURITY.md', 'SECURITY.pt-BR.md'),
    @('CODE_OF_CONDUCT.md', 'CODE_OF_CONDUCT.pt-BR.md')
)
foreach ($file in $english) { $pairs += ,@("docs/en/$($file.Name)", "docs/pt-BR/$($file.Name)") }
$paths = @('docs/README.md', 'docs/assets/README.md')
foreach ($pair in $pairs) {
    $texts = @($pair | ForEach-Object { [IO.File]::ReadAllText((Join-Path $root $_)) })
    if (!$texts[0].Contains('Português (Brasil)') -or !$texts[1].Contains('English')) { throw "Missing language navigation: $($pair[0])" }
    $commands = @($texts | ForEach-Object {
        (@([regex]::Matches($_, '(?s)```powershell\s*\r?\n(.*?)```') | ForEach-Object { $_.Groups[1].Value.Replace("`r`n", "`n").Trim() }) -join "`n---`n")
    })
    if ($commands[0] -cne $commands[1]) { throw "Translated commands differ: $($pair[0])" }
    $paths += $pair
}
$linkCount = 0
foreach ($path in ($paths | Sort-Object -Unique)) {
    $full = Join-Path $root $path
    $text = [IO.File]::ReadAllText($full)
    foreach ($match in [regex]::Matches($text, '\]\(([^)]+)\)')) {
        $href = $match.Groups[1].Value
        if ($href -match '^(https?://|mailto:|#)') { continue }
        $target = [IO.Path]::GetFullPath(($href -split '#', 2)[0], (Split-Path $full -Parent))
        if (!$target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Link escaped repository: $path" }
        if ($target.StartsWith((Join-Path $root 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Public guide depends on local artifacts: $path" }
        if (!(Test-Path -LiteralPath $target)) { throw "Missing link target in $path : $href" }
        $linkCount++
    }
}
[pscustomobject]@{ Valid=$true; LanguagePairs=$pairs.Count; PublicDocuments=@($paths | Sort-Object -Unique).Count; LocalLinksChecked=$linkCount; CommandsMatch=$true; HistoricalRecordsTranslated=$false }
