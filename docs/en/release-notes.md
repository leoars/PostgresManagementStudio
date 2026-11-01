# v0.1.0-alpha.2 — Windows x64

English | [Português (Brasil)](../pt-BR/release-notes.md)

Second public pre-release of PGMS and the first one built by continuous integration. It is an alpha for evaluation with synthetic/disposable data. Build label: M1008, the same application source as v0.1.0-alpha.1; this release changes how the binaries are produced, verified and installed, not the application code. Original work is MIT; dependency notices and licenses accompany the package.

## What changed since v0.1.0-alpha.1

- **Built by GitHub Actions.** The ZIP and the graphical installer come from the [release workflow](../../.github/workflows/release.yml) on a GitHub-hosted Windows runner, from the tagged commit, with deterministic build settings (`ContinuousIntegrationBuild`, `Deterministic`) and a locked NuGet restore. v0.1.0-alpha.1 was built on the maintainer's machine.
- **Build provenance attestation.** The ZIP and the installer carry a GitHub artifact attestation that binds each file to this repository, the workflow and the commit. Verify it with the GitHub CLI (see below).
- **Graphical installer in the same release and from the same payload.** `PGMS-0.1.0-alpha.2-Setup-x64.exe` (Inno Setup 7.1.1, English/pt-BR, current user or all users, native uninstall) is built in the same run from the exact payload packed in the ZIP. In v0.1.0-alpha.1 the installer was added to the release later.
- **One checksum file.** `SHA256SUMS.txt` covers every asset; there are no per-asset `.sha256` files.
- **No PowerShell download installer.** `Install-PGMS-<version>.ps1` is not published for this release; the managed PowerShell 7 scripts inside the ZIP (install, launch, rollback, uninstall) remain available.
- **Documentation.** New README landing (value proposition, Install / Build from source), Windows SmartScreen guidance with checksum and attestation verification, release process documented in [development](development.md).

The application itself is unchanged: the `src/` tree is identical to v0.1.0-alpha.1 (milestone M1008). Public-source changes since alpha.1 are limited to the graphical installer sources, build/test scripts, CI workflows, community files and documentation.

## Downloads

From [this release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.2):

| File | Purpose |
| --- | --- |
| `PGMS-0.1.0-alpha.2-Setup-x64.exe` | Graphical installer (English/pt-BR). Current user without administrator rights, or all users with elevation. No PowerShell or separate .NET runtime required. |
| `PGMS-0.1.0-alpha.2-win-x64.zip` | Portable application with .NET/WPF runtime. Extract the entire folder and run PGMS.exe. Includes the managed PowerShell 7 scripts. |
| `SHA256SUMS.txt` | SHA256 of every asset in this release. Hashes check correspondence, not authorship. |
| `package-manifest.json` | SHA256 manifest of every file inside the ZIP, as checked by `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | These notes. |

## Verify what you downloaded

The binaries are not code-signed, so Windows SmartScreen may warn on first launch. Before choosing **Run anyway**, confirm the checksum and the provenance:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.2-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare the hashes with `SHA256SUMS.txt`. The second command needs the [GitHub CLI](https://cli.github.com/) and confirms that the file was produced by this repository's release workflow at the tagged commit; run it for the ZIP as well. Code signing through the SignPath Foundation open source program is planned; no date is promised.

## Scope

SQL editor and independent sessions, Object Explorer, bounded Results/Messages and CSV/JSON export, reviewed PostgreSQL administration, identity/privileges, session monitoring and logical backup/restore. PostgreSQL 17/18 and same-major client tools are required for database operations and backup/restore; server/tools are not bundled. SSMS is neither required nor distributed.

## Validation

Application behavior: the M1008 source previously passed the complete PostgreSQL 17.11 regression (**2,115 integration + 3,072 WPF = 5,187 checks**) on an isolated loopback cluster, as recorded in the v0.1.0-alpha.1 notes and in [validation](validation.md). That suite was not rerun for this release because the application source did not change; it does not run in CI.

Release pipeline: the workflow validates that the tag matches `installer/release.json`, `Directory.Build.props` and `BuildInfo.cs`; publishes with `Publish.ps1 -SelfContained` (SDK selected by `global.json`, bundled .NET/WPF 10.0.14, ReadyToRun); runs `Verify-Package.ps1`, `Test-Package.ps1` and `Test-PackageGuides.ps1`; creates the ZIP with `New-PackageArchive.ps1`; installs the pinned, hash-verified Inno Setup 7.1.1 and builds the installer; runs an informational launch smoke on the runner; attests the ZIP and the installer; and opens a draft pre-release that the maintainer reviews before publishing. A dry run of the same workflow on the preceding commit (test tag `v0.1.0-alpha.1+ci.1`, later deleted) completed, and its assets passed checksum comparison, `gh attestation verify`, `Verify-Package.ps1`, `Test-PackageArchive.ps1`, `Test-PackageGuides.ps1`, `Test-PackageLaunch.ps1` and the 17 current-user checks of `Test-GraphicalInstaller.ps1` on the maintainer's machine. The same local verification is repeated on this release's draft assets before publication and reported in the release pull request.

All-users (elevated) installation, a clean-Windows VM installation and physical DPI/keyboard/screen-reader observations are not newly certified by this release.

## Known limits

Windows x64 only. Unsigned executable and installer; SmartScreen warns. No MSI, automatic update or production-support guarantee. Full SSMS parity is not claimed. Results/recovery are bounded; large SQL lines may pause rendering. Cancellation is not proof of rollback. Restore SQL must be trusted and can control transactions, reconnect or execute local psql commands. DPAPI state is tied to the Windows user. The frozen manifest hash in `installer/release.json` describes a locally built payload, not the CI payload; the CI build compares milestone and product version instead. See [validation](validation.md) and [security](../../SECURITY.md).

## Previous release

v0.1.0-alpha.1 (2026-10-31) remains available with its original assets and notes; it was built on the maintainer's machine and has checksums but no attestation.
