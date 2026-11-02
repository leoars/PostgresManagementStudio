# v0.1.0-alpha.3 — Windows x64

English | [Português (Brasil)](../pt-BR/release-notes.md)

Third public pre-release of PGMS. It is an alpha for evaluation with synthetic/disposable data. Build label: M1022. Unlike v0.1.0-alpha.2, this release changes the application: it adds Query Statistics over `pg_stat_statements`, stops `$n` placeholders from closing the query session and gives grid rows short screen-reader names. The binaries are built and attested by the same [release workflow](../../.github/workflows/release.yml) as alpha.2. Original work is MIT; dependency notices and licenses accompany the package.

## What changed since v0.1.0-alpha.2

- **Query Statistics (View → Query Statistics…).** Read-only analysis of `pg_stat_statements` for the connected database, with a purpose similar to SQL Server Query Store: calls, execution time (total, mean, minimum, maximum, standard deviation), rows, cache hit %, shared/temporary blocks, I/O time and WAL bytes per normalized statement. Server-side filters and ordering with a 1,000-row limit, sortable headers, a top-N chart beside the grid, cumulative/since-baseline/last 5, 15 or 60 minute windows, **Show Plan** with `EXPLAIN (GENERIC_PLAN)` in a new query tab, confirmed per-statement and full resets, optional auto-refresh and filters saved per connection target. PGMS never installs or configures the extension; the window explains what the server is missing. See the [user guide](user-guide.md#query-statistics).
- **`$n` placeholders no longer close the session.** Executing, explaining or analyzing SQL that contains `$1`-style placeholders used to fail with protocol error 08P01, after which the driver closed the tab's connection. PGMS now detects placeholders before sending (ignoring literals, quoted identifiers, comments and dollar quotes; `PREPARE` and function/procedure bodies are exempt), refuses the statement with **Go to Error** on the placeholder and keeps the session open. **Estimated Plan** switches to a generic plan.
- **Shorter screen-reader names for grid rows.** Every WPF grid, including query results, names each row from its first visible columns (for example `Query ID 42, Database app, …`) instead of exposing the whole underlying record.

## Downloads

From [this release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.3):

| File | Purpose |
| --- | --- |
| `PGMS-0.1.0-alpha.3-Setup-x64.exe` | Graphical installer (English/pt-BR). Current user without administrator rights, or all users with elevation. No PowerShell or separate .NET runtime required. |
| `PGMS-0.1.0-alpha.3-win-x64.zip` | Portable application with .NET/WPF runtime. Extract the entire folder and run PGMS.exe. Includes the managed PowerShell 7 scripts. |
| `SHA256SUMS.txt` | SHA256 of every asset in this release. Hashes check correspondence, not authorship. |
| `package-manifest.json` | SHA256 manifest of every file inside the ZIP, as checked by `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | These notes. |

## Verify what you downloaded

The binaries are not code-signed, so Windows SmartScreen may warn on first launch. Before choosing **Run anyway**, confirm the checksum and the provenance:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.3-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare the hashes with `SHA256SUMS.txt`. The second command needs the [GitHub CLI](https://cli.github.com/) and confirms that the file was produced by this repository's release workflow at the tagged commit; run it for the ZIP as well. Code signing through the SignPath Foundation open source program is planned; no date is promised.

## Scope

SQL editor and independent sessions, Object Explorer, bounded Results/Messages and CSV/JSON export, reviewed PostgreSQL administration, identity/privileges, session monitoring, query statistics and logical backup/restore. PostgreSQL 17/18 and same-major client tools are required for database operations and backup/restore; server/tools are not bundled. Query Statistics also requires the `pg_stat_statements` extension on the server. SSMS is neither required nor distributed.

## Validation

Application behavior: the M1022 source passed the complete PostgreSQL 17.11 regression of this repository (**2,236 integration + 3,315 WPF = 5,551 checks**) on an isolated loopback cluster started with `pg_stat_statements` preloaded; the host PostgreSQL service is not used. The focused Query Statistics suite (`Test-Integration.ps1 -QueryStatisticsOnly`) passed 90 integration + 224 WPF checks. The regression does not run in CI. The Query Statistics window was also compared with the SQL Server Query Store of SSMS 22.11 on an identical synthetic workload; differences are documented in [validation](validation.md).

Release pipeline: unchanged from v0.1.0-alpha.2. The workflow validates that the tag matches `installer/release.json`, `Directory.Build.props` and `BuildInfo.cs`; publishes with `Publish.ps1 -SelfContained`; runs `Verify-Package.ps1`, `Test-Package.ps1` and `Test-PackageGuides.ps1`; creates the ZIP; builds the installer with the pinned, hash-verified Inno Setup 7.1.1; runs an informational launch smoke on the runner; attests the ZIP and the installer; and opens a draft pre-release that the maintainer reviews before publishing. The draft assets are verified locally before publication, as for alpha.2, and the result is reported in the release pull request.

All-users (elevated) installation, a clean-Windows VM installation and physical DPI/keyboard/screen-reader observations of the new window are not newly certified by this release.

## Known limits

Windows x64 only. Unsigned executable and installer; SmartScreen warns. No MSI, automatic update or production-support guarantee. Full SSMS parity is not claimed. Query Statistics is a snapshot of cumulative server counters, not continuous monitoring: sliding windows sample only while the window is open, minimum/maximum are not available for a window and PostgreSQL has no per-statement CPU time. The placeholder check assumes `standard_conforming_strings = on` and offers no parameter-value dialog. Results/recovery are bounded; large SQL lines may pause rendering. Cancellation is not proof of rollback. Restore SQL must be trusted and can control transactions, reconnect or execute local psql commands. DPAPI state is tied to the Windows user. The frozen manifest hash in `installer/release.json` describes a locally built payload, not the CI payload; the CI build compares milestone and product version instead. See [validation](validation.md) and [security](../../SECURITY.md).

## Previous releases

v0.1.0-alpha.2 (2026-11-01, first CI-built release with attestation) and v0.1.0-alpha.1 (2026-10-31) remain available with their original assets and notes.
