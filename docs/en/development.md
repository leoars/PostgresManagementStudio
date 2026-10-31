# Development

English | [Português (Brasil)](../pt-BR/development.md)

## Build and run

Use Windows x64, PowerShell 7 and the SDK selected by `global.json`. Run commands from the repository root. `Build.ps1` restores with locked dependencies, then builds the solution. Nullable analysis and warnings-as-errors are enabled. Neither SSMS nor REA is required to build or run PGMS.

```powershell
./scripts/Build.ps1 -Configuration Release
dotnet run --project src/PGMS.Desktop
./scripts/Test-PublicDocumentation.ps1
```

## Test in isolation

Database tests require local PostgreSQL 17 or18 server binaries and matching tools. The default binaries path is `C:\Program Files\PostgreSQL\17\bin`; use `-PostgresBin` for another installation. The harness creates its own loopback cluster under `artifacts/postgres-test`, uses 127.0.0.1:55439 by default and stops it afterward. If that port is occupied, use `-TestPort` with a free loopback port; the runner still creates its own cluster and refuses to attach to another server. It must not share a running PostgreSQL service or user database. Data directories are separated by major version. Desktop tests require a Windows desktop; some probes use UI Automation against their own child processes.

```powershell
./scripts/Test-Integration.ps1
./scripts/Test-Integration.ps1 -PostgresBin 'C:\Program Files\PostgreSQL\18\bin'
./scripts/Test-Integration.ps1 -TestPort 55441
./scripts/Test-Integration.ps1 -IdentityColumnsOnly
./scripts/Test-Integration.ps1 -BackupFormatsOnly
./scripts/Test-Package.ps1
```

Choose only one focused suite per invocation. Some fixtures intentionally create junctions and may need filesystem permissions unavailable in a sandbox. Native clipboard/physical keyboard/screen-reader tests have separate isolation requirements; do not run them against a personal desktop or infer a physical pass from ordinary WPF checks. Read the relevant script before invoking an interactive probe.

## Package

`Publish.ps1 -SelfContained` builds a portable ReadyToRun package using the current `BuildInfo.Milestone`, copies notices/licenses and creates a SHA256 manifest. `New-PackageArchive.ps1` validates extraction before writing a ZIP and checksum. Existing output is refused. A package is identified by its manifest/hash as well as its build label; local rebuilds are not necessarily identical binaries. Publishing source does not publish a prebuilt ZIP. See [getting started](getting-started.md).

## Change workflow

Read [CONTRIBUTING.md](../../CONTRIBUTING.md) and [AGENTS.md](../../AGENTS.md). Keep public English/pt-BR guides synchronized. Report validation actually run and its limitations. Record relevant changes and validation in the pull request. Never commit profiles, tokens, private keys, dumps, build outputs or local acceptance artifacts. Gitleaks can scan history with `gitleaks git . --log-opts="--all --full-history" --redact=100`. This source repository starts with a new history and no historical secret-scan exceptions.
