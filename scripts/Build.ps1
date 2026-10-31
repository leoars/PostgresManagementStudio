param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore PGMS.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build PGMS.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
