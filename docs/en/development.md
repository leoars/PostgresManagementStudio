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

When publishing a release, upload one `SHA256SUMS.txt` that covers every binary asset (ZIP, PowerShell installer and graphical installer EXE) instead of per-asset checksum files, and do not replace assets or checksums after publication; publish a new tag instead.

## Release

Release binaries come from [release.yml](../../.github/workflows/release.yml) on a GitHub-hosted Windows runner, not from a developer machine. Before tagging, declare the same version in `Directory.Build.props`, `installer/release.json` (`Version`, `NumericVersion`, `Milestone`) and `src/PGMS.Desktop/BuildInfo.cs`; the workflow fails when the tag (`v` plus the version) and these files disagree. Pushing a tag `v*`, or running the workflow manually with the tag name, publishes the ReadyToRun payload with `Publish.ps1 -SelfContained` under deterministic build settings, runs `Verify-Package.ps1`, `Test-Package.ps1` and `Test-PackageGuides.ps1`, creates the ZIP with `New-PackageArchive.ps1`, installs the pinned, hash-verified Inno Setup 7.1.1 in portable mode and builds the installer with `Build-GraphicalInstaller.ps1 -SkipFrozenManifest` (the frozen manifest hash in `release.json` describes a payload built locally; in CI the payload comes from the run itself, so only milestone and product version are compared). It then writes `SHA256SUMS.txt`, attests build provenance for the ZIP and the installer and creates a **draft** pre-release with the notes from `docs/en` and `docs/pt-BR`. Publishing the draft, and any change to an existing release, remains a manual decision; a release that already exists for the tag makes the workflow fail. A manual run whose tag carries build metadata (`v0.1.0-alpha.1+ci.1`) produces a test draft that must be deleted after review. Verify an asset produced by the workflow with:

```powershell
gh attestation verify .\<asset> --repo leoars/PostgresManagementStudio
```

The jobs `sign-binaries` and `sign-installer` are reserved for the SignPath Foundation open source program and stay skipped until the repository variable `SIGNPATH_ENABLED` is `true`; they then require the secret `SIGNPATH_API_TOKEN` and the variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG` and `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`. Application binaries are signed before they enter the ZIP and the installer, the installer is signed last, and checksums and attestations cover the final files.

## Change workflow

Read [CONTRIBUTING.md](../../CONTRIBUTING.md) and [AGENTS.md](../../AGENTS.md). Keep public English/pt-BR guides synchronized. Report validation actually run and its limitations. Record relevant changes and validation in the pull request. GitHub Actions ([build.yml](../../.github/workflows/build.yml)) runs `Build.ps1 -Configuration Release` and `Test-PublicDocumentation.ps1` on Windows for pushes and pull requests; database and WPF suites are not run there and must be reported manually. Dependabot opens weekly pull requests for NuGet packages and pinned actions. Never commit profiles, tokens, private keys, dumps, build outputs or local acceptance artifacts. Gitleaks can scan history with `gitleaks git . --log-opts="--all --full-history" --redact=100`. This source repository starts with a new history and no historical secret-scan exceptions.
