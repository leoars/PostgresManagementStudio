param(
    [ValidatePattern('^M[1-9][0-9]*$')][string]$Milestone,
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $buildInfo = Get-Content -LiteralPath 'src/PGMS.Desktop/BuildInfo.cs' -Raw
    if ($buildInfo -notmatch 'Milestone = "(M[0-9]+)"') { throw 'Build milestone missing.' }
    $currentMilestone = $Matches[1]
    if (!$Milestone) { $Milestone = $currentMilestone }
    if ($Milestone -cne $currentMilestone) { throw 'Milestone must match BuildInfo.cs; historical labels cannot relabel the current binary.' }
    $folder = $Milestone
    if ($SelfContained) { $folder += '-win-x64' }
    $outputPath = Join-Path $projectRoot "artifacts/$folder"
    if (Test-Path -LiteralPath $outputPath) { throw "Package already exists: $outputPath. Preserve it or choose a new build milestone." }
    & ./scripts/Build.ps1 -Configuration Release
    $stagingPath = "$outputPath.$([Guid]::NewGuid().ToString('N')).partial"
    $publishArgs = @('--self-contained', 'false')
    if ($SelfContained) {
        dotnet restore src/PGMS.Desktop/PGMS.Desktop.csproj --locked-mode -r win-x64 -p:PublishReadyToRun=true
        if ($LASTEXITCODE -ne 0) { throw 'Runtime restore failed' }
        # ReadyToRun precompiles PGMS and its libraries; measured ~30% shorter first open of very long SQL lines.
        $publishArgs = @('-r', 'win-x64', '--self-contained', 'true', '-p:PublishReadyToRun=true')
    }
    dotnet publish src/PGMS.Desktop/PGMS.Desktop.csproj -c Release --no-restore -o $stagingPath @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed; staging retained at $stagingPath" }
    Copy-Item -LiteralPath 'LICENSE', 'THIRD-PARTY-NOTICES.md' -Destination $stagingPath
    Copy-Item -LiteralPath 'docs/pt-BR/distribution.md' -Destination (Join-Path $stagingPath 'LEIA-ME.md')
    Copy-Item -LiteralPath 'docs/en/distribution.md' -Destination (Join-Path $stagingPath 'READ-ME.md')
    New-Item -ItemType Directory -Path (Join-Path $stagingPath 'licenses') | Out-Null
    Get-ChildItem -LiteralPath 'docs/licenses' -File | Copy-Item -Destination (Join-Path $stagingPath 'licenses')
    if ($SelfContained) {
        $assets = Get-Content -LiteralPath 'src/PGMS.Desktop/obj/project.assets.json' -Raw | ConvertFrom-Json
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $stagingPath 'PGMS.runtimeconfig.json') -Raw | ConvertFrom-Json
        foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
            $packageName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
            $packagePath = $null
            foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
                $candidate = Join-Path $packageRoot "$packageName/$($framework.version)"
                if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
            }
            if (!$packagePath) { throw "Runtime license source missing: $packageName" }
            $notices = @(Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' })
            if (!($notices | Where-Object Name -Match '^LICENSE')) { throw "Runtime license missing: $packageName" }
            foreach ($notice in $notices) {
                Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $stagingPath "licenses/$packageName-$($notice.Name)")
            }
        }
    }
    if (!(Test-Path -LiteralPath (Join-Path $stagingPath 'PGMS.exe'))) { throw 'Executable missing.' }
    if ($SelfContained -and !(Test-Path -LiteralPath (Join-Path $stagingPath 'coreclr.dll'))) { throw 'Bundled runtime missing.' }
    if ($SelfContained) {
        foreach ($assembly in 'PGMS.dll', 'ICSharpCode.AvalonEdit.dll', 'AvalonDock.dll') {
            $reader = [System.Reflection.PortableExecutable.PEReader]::new([System.IO.File]::OpenRead((Join-Path $stagingPath $assembly)))
            try { $readyToRun = $reader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size -gt 0 }
            finally { $reader.Dispose() }
            if (!$readyToRun) { throw "ReadyToRun code missing: $assembly" }
        }
    }
    Copy-Item -LiteralPath 'scripts/PackageTools.ps1', 'scripts/Verify-Package.ps1' -Destination $stagingPath
    Copy-Item -LiteralPath 'scripts/InstallationTools.ps1', 'scripts/Install-Pgms.ps1', 'scripts/Set-PgmsActiveVersion.ps1', 'scripts/Start-Pgms.ps1', 'scripts/Uninstall-Pgms.ps1', 'scripts/Get-PgmsInstallation.ps1', 'scripts/Test-SandboxInstallation.ps1' -Destination $stagingPath
    . ./scripts/PackageTools.ps1
    New-PgmsPackageManifest $stagingPath $Milestone ([bool]$SelfContained) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $outputPath)
    Write-Host "Package created: $outputPath"
} finally { Pop-Location }
