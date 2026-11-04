# Getting started

English | [Português (Brasil)](../pt-BR/getting-started.md)

[Download v0.1.0-alpha.4](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.4): [graphical installer](graphical-installer.md), portable ZIP or PowerShell 7 installer. The graphical installer offers current-user and all-users modes and does not require PowerShell. See [release notes](release-notes.md).

### Windows SmartScreen warning

PGMS releases are not code-signed yet, so Windows may show "Windows protected your PC" the first time you run the installer or `PGMS.exe`. Choose **More info** and then **Run anyway**. Before doing so, confirm the download matches the checksums published with the release:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
```

Compare the hashes with `SHA256SUMS.txt` in the release. Starting with v0.1.0-alpha.2 the assets are built by the [release workflow](../../.github/workflows/release.yml) on GitHub Actions and carry a build provenance attestation, so you can also confirm that the ZIP or installer came from that workflow and this repository (requires the [GitHub CLI](https://cli.github.com/); see [development](development.md)):

```powershell
gh attestation verify .\PGMS-0.1.0-alpha.4-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

v0.1.0-alpha.1 was built on the maintainer's machine and has checksums only. Code signing through the SignPath Foundation open source program is planned.

## Requirements

PGMS targets Windows x64. Building requires PowerShell 7 and a .NET 10 SDK compatible with [global.json](../../global.json): 10.0.400 with latest-patch roll-forward. Dependencies are pinned in NuGet lockfiles. The M1030 package was built with SDK 10.0.403 and bundles .NET/WPF 10.0.14; a rebuild may use different installed runtime packs.

PostgreSQL 16–18 with same-major client tools is the supported database scope. The complete regression runs on 17.11; Activity Monitor and Query Statistics suites also ran on 16.15 and 18.6. On PostgreSQL 16, Query Statistics needs `pg_stat_statements` 1.10 or later. A server is not needed just to open the shell. Backup/restore requires external pg_dump, pg_restore, psql and pg_dumpall as applicable; PGMS installs none of them.

```powershell
./scripts/Build.ps1
dotnet run --project src/PGMS.Desktop
```

## Portable package

```powershell
./scripts/Publish.ps1 -SelfContained
./scripts/Verify-Package.ps1 -PackagePath artifacts/M1030-win-x64
./scripts/New-PackageArchive.ps1 -PackagePath artifacts/M1030-win-x64
```

Paths follow `BuildInfo.Milestone`; replace M1030 if the build label changes. Existing package folders and ZIPs are refused rather than overwritten. A fresh clone contains neither local ZIPs nor acceptance artifacts. Extract the entire package and run PGMS.exe, retaining adjacent libraries and licenses. Without `-SelfContained`, the package needs an installed .NET10 Desktop Runtime. No SSMS is required.

Optional managed installation/update/rollback uses the packaged PowerShell 7 scripts. Read `LEIA-ME.md` shipped with the exact package before using them. Portable execution does not require PowerShell 7. The current ZIP has no MSI, code signature or automatic updater; SmartScreen behavior has not been tested.

## Local state

Profiles, layout and recovery normally live under `%LOCALAPPDATA%/PGMS`. Saved passwords and drafts use Windows user-scoped DPAPI. They are not portable credentials. For an isolated manual trial, set a new state directory in the current PowerShell process before launching:

```powershell
$env:PGMS_STATE_DIRECTORY = Join-Path $PWD 'artifacts/manual-trial/state'
dotnet run --project src/PGMS.Desktop
Remove-Item Env:PGMS_STATE_DIRECTORY
```

Close PGMS before copying or changing its state. Keep copies of important SQL files separately. See the [user guide](user-guide.md) for connection and recovery behavior.
