# v0.1.0-alpha.4 — Windows x64

English | [Português (Brasil)](../pt-BR/release-notes.md)

Fourth public pre-release of PGMS. It is an alpha for evaluation with synthetic/disposable data. Build label: M1030. This release rebuilds the Activity Monitor as a document tab with the SSMS panels, adds concurrent detach and sub-partitions to the Partitions dialog and supports PostgreSQL 16. The binaries are built and attested by the same [release workflow](../../.github/workflows/release.yml) as alpha.2 and alpha.3. Original work is MIT; dependency notices and licenses accompany the package.

## What changed since v0.1.0-alpha.3

- **Activity Monitor as a document tab with six panels.** **View → Activity Monitor** (`Ctrl+Alt+A`) opens one tab per server and role, reused when opened again. Collapsible panels as in SSMS: **Overview** (charts of active sessions, waiting sessions, database I/O from `pg_stat_io` and transactions per second), **Processes**, **Resource Waits** (sampled wait categories), **Data File I/O** (per database), **Recent Expensive Queries** (the change in `pg_stat_statements` between refreshes) and **Active Expensive Queries** (with **Edit Query Text** and **Show Execution Plan**, an estimated plan that does not execute the query). The Overview context menu sets the interval and pauses, resumes or refreshes the monitor; while paused the header reads "Overview (paused)". See the [user guide](user-guide.md#activity-monitor).
- **Session list closer to SSMS.** Host, **Blocked by PID** and **Head blocker** columns (computed from server-wide `pg_locks`), header tooltips, a row menu (**Details…**, **Cancel Query**, **Terminate Session…**), a **Session Details** dialog with the most recent query, server-side filters and pages by PID (First/Previous/Next). Auto-refresh starts on, every 10 seconds, with intervals from 1 second to 1 hour.
- **Partitions.** **Detach concurrently** runs `DETACH PARTITION … CONCURRENTLY` outside a transaction, without blocking queries, after checking for a DEFAULT partition and for another pending detach; a pending partition is finished with `FINALIZE`. The dialog also creates a partition that is itself partitioned (**Sub-partition by**, keyed by columns of the parent), attaches partitioned tables and detaches partitioned partitions with their sub-tree.
- **PostgreSQL 16.** The supported scope is now PostgreSQL 16 to 18. Query Statistics and Recent Expensive Queries read `pg_stat_statements` 1.10 on PostgreSQL 16; versions older than 1.10 are refused with the `ALTER EXTENSION … UPDATE` they need.
- **Visual fixes.** Inactive document tabs use dark text and the inactive grid selection stays blue, as in SSMS.

## Downloads

From [this release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.4):

| File | Purpose |
| --- | --- |
| `PGMS-0.1.0-alpha.4-Setup-x64.exe` | Graphical installer (English/pt-BR). Current user without administrator rights, or all users with elevation. No PowerShell or separate .NET runtime required. |
| `PGMS-0.1.0-alpha.4-win-x64.zip` | Portable application with .NET/WPF runtime. Extract the entire folder and run PGMS.exe. Includes the managed PowerShell 7 scripts. |
| `SHA256SUMS.txt` | SHA256 of every asset in this release. Hashes check correspondence, not authorship. |
| `package-manifest.json` | SHA256 manifest of every file inside the ZIP, as checked by `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | These notes. |

## Verify what you downloaded

The binaries are not code-signed, so Windows SmartScreen may warn on first launch. Before choosing **Run anyway**, confirm the checksum and the provenance:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.4-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare the hashes with `SHA256SUMS.txt`. The second command needs the [GitHub CLI](https://cli.github.com/) and confirms that the file was produced by this repository's release workflow at the tagged commit; run it for the ZIP as well. Code signing through the SignPath Foundation open source program is planned; no date is promised.

## Scope

SQL editor and independent sessions, Object Explorer, bounded Results/Messages and CSV/JSON export, reviewed PostgreSQL administration (partitions included), identity/privileges, Activity Monitor, query statistics and logical backup/restore. PostgreSQL 16 to 18 and same-major client tools are required for database operations and backup/restore; server/tools are not bundled. Query Statistics and Recent Expensive Queries also require the `pg_stat_statements` extension on the server. SSMS is neither required nor distributed.

## Validation

Application behavior: the M1030 source passed the complete PostgreSQL 17.11 regression of this repository (**2,318 integration + 3,429 WPF = 5,747 checks**) on an isolated loopback cluster started with `pg_stat_statements` preloaded; the host PostgreSQL service is not used. On the same source, the focused Activity Monitor suite (`Test-Integration.ps1 -ActivityOnly`) passed 71 integration + 152 WPF checks on PostgreSQL 16.15 and 18.6; the Query Statistics suite passed on 16.15 and 18.6 on the M1029 source, and the Query Statistics code has not changed since. The regression does not run in CI. The Activity Monitor was compared twice with the one in SSMS 22.11 under the same blocking workload; remaining differences are listed in [validation](validation.md).

Release pipeline: unchanged from v0.1.0-alpha.2. The workflow validates that the tag matches `installer/release.json`, `Directory.Build.props` and `BuildInfo.cs`; publishes with `Publish.ps1 -SelfContained`; runs `Verify-Package.ps1`, `Test-Package.ps1` and `Test-PackageGuides.ps1`; creates the ZIP; builds the installer with the pinned, hash-verified Inno Setup 7.1.1; runs an informational launch smoke on the runner; attests the ZIP and the installer; and opens a draft pre-release that the maintainer reviews before publishing. The draft assets are verified locally before publication, and the result is reported in the release pull request.

All-users (elevated) installation, a clean-Windows VM installation, a complete regression on PostgreSQL 16 and 18 and physical DPI/keyboard/screen-reader observations of the new screens are not newly certified by this release.

## Known limits

Windows x64 only. Unsigned executable and installer; SmartScreen warns. No MSI, automatic update or production-support guarantee. Full SSMS parity is not claimed. Activity Monitor: Data File I/O is per database, not per file (PostgreSQL has no per-file statistics); Resource Waits is sampled at each refresh, not cumulative; session pages are separate reads and sorting applies within a page; nothing was tested with more than 1,000 sessions. Query Statistics is a snapshot of cumulative server counters, not continuous monitoring; with `pg_stat_statements` 1.10 a time window can count low a statement that was removed and tracked again. The placeholder check assumes `standard_conforming_strings = on` and offers no parameter-value dialog. Results/recovery are bounded; large SQL lines may pause rendering. Cancellation is not proof of rollback. Restore SQL must be trusted and can control transactions, reconnect or execute local psql commands. DPAPI state is tied to the Windows user. The frozen manifest hash in `installer/release.json` describes a locally built payload, not the CI payload; the CI build compares milestone and product version instead. See [validation](validation.md) and [security](../../SECURITY.md).

## Previous releases

v0.1.0-alpha.3 (2026-11-02, Query Statistics), v0.1.0-alpha.2 (2026-11-01, first CI-built release with attestation) and v0.1.0-alpha.1 (2026-10-31) remain available with their original assets and notes.
