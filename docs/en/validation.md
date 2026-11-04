# Validation and limits

English | [Português (Brasil)](../pt-BR/validation.md)

Current pre-release: v0.1.0-alpha.4, build label M1030 (adds the Activity Monitor document tab and panels, concurrent detach and sub-partitions, and PostgreSQL 16 support to the M1022 source of alpha.3; built by the release workflow on GitHub Actions with a build provenance attestation). Milestone labels are internal identifiers, not guarantees that a local rebuild has the same binary identity. The initial public source import used M1002. The private operational history and raw acceptance evidence are intentionally not included.

The imported implementation previously passed 5,187 automated checks (2,115 PostgreSQL integration and 3,072 WPF), plus package opening, installed profile preservation and three-draft recovery through rollback/restart. Those are historical observations, not newly executed tests for every source revision. Reproduce current behavior with the public build and test scripts; report your actual environment and results. The clean-import checks for the initial public revision are recorded below after execution.

Known limits: Windows x64; PostgreSQL 16–18 with same-major tools, complete regression on 17.11 and focused suites on 16.15/18.6; 15 and earlier unsupported. No full SSMS parity. Huge SQL lines may pause rendering. Results and recovery have bounded retention. Cancellation is not proof of rollback. DPAPI is user-bound. The package is unsigned, without MSI/automatic update; SmartScreen is not certified. DPI100/125/150/175% were exercised internally;200% was accepted without execution. Automatic reappearance after disconnect, cross-user DPAPI portability and a new clean-Windows test of this import are not certified. External clipboard/keyboard/screen-reader observations are historical and not reproduced by ordinary WPF tests.

```powershell
./scripts/Build.ps1 -Configuration Release
./scripts/Test-Integration.ps1
./scripts/Test-Package.ps1
./scripts/Test-Installation.ps1
./scripts/Test-PublicDocumentation.ps1
```

The database harness creates a disposable loopback cluster under this checkout's ignored artifacts directory, exclusively loopback (port 55439 by default, configurable with -TestPort), and stops it afterward. Never use personal or production databases for these tests. See [development](development.md).

## Initial curated-source checks — 2026-10-31

Release build passed without warnings/errors. There were 157 focused desktop file checks, 22 package checks and 46 installation-fixture checks. Public documentation checks covered 12 language pairs and local links. A locally built self-contained package passed manifest/license/bilingual-guide checks, loaded its own runtime, and closed normally with exit0 using isolated state. No binary release was uploaded.

The complete PostgreSQL regression was attempted but stopped before server startup because Windows denied binding the isolated test port 55439 (socket access error). A separate loopback probe reproduced this; dynamic-port binding worked. The full 5,187 historical checks are therefore not claimed as rerun for this import. The disposable cluster was not running afterward. No firewall settings or user databases were changed. Gitleaks found no alerts in the curated source, with no historical exceptions.

## v0.1.0-alpha.1 / M1008 — 2026-10-31

A fresh complete run passed 2,115 integration +3,072 WPF =5,187 checks on PostgreSQL 17.11 using the disposable cluster on 127.0.0.1:55441; application source was unchanged across the run, the cluster stopped and the previous test-port environment was restored. Eleven port-parser checks and two invalid runner-port checks passed separately. The default port was occupied, so test-only endpoint configuration was added without changing firewall settings or stopping unrelated processes.

The first full attempt hit a race in a test-only readiness file: the parent observed the file before its writer closed. Test children now publish readiness by renaming a closed temporary file. Focused process checks passed 26+20, followed by the successful full rerun above; the earlier failure was not counted as a pass. These harness changes do not change the distributed application's database behavior. Version metadata/build label and release packaging changed for this pre-release.

Package and release-installer results are recorded in the [release notes](release-notes.md). Historical PostgreSQL 18/physical/VM observations are not reclassified as new tests of this binary. No new clean-Windows or SmartScreen certification is claimed.

## v0.1.0-alpha.3 / M1022 — 2026-11-02

A complete run passed 2,236 integration + 3,315 WPF = 5,551 checks on PostgreSQL 17.11. The disposable loopback cluster now starts with `shared_preload_libraries=pg_stat_statements`; the host service is not touched, and the cluster stopped afterward. The focused suite `Test-Integration.ps1 -QueryStatisticsOnly` passed 90 integration + 224 WPF checks. Integration checks cover detection without the extension (nothing is installed), ordinary roles versus `pg_read_all_stats`, server-side filters/order and the 1,000-row sentinel, per-statement and full resets refused or authorized by PostgreSQL, baselines and windows, generic plans, and placeholder detection across literals, comments and dollar quotes with the session kept open. WPF checks cover loading, cancellation under a real lock, preserved selection, header ordering, chart, window selector, saved preferences (including a corrupt file left untouched), row accessibility names and minimum-size layout.

The window was compared, during development, with the Query Store of SSMS 22.11 on an identical synthetic workload against SQL Server LocalDB and the disposable PostgreSQL cluster. Both listed the same statements with matching counts. Differences found then: one window instead of seven Query Store reports; server-wide cumulative counters instead of per-database time intervals; no chart, plan, logical reads or header sorting; full records exposed as row names to screen readers. Header sorting, I/O and block metrics, generic plans, the top-N chart, time windows and short row names were added afterward. Remaining differences: one window instead of separate reports, windows computed by subtracting samples rather than stored intervals, and no per-statement CPU time, which PostgreSQL does not record. Physical DPI and screen-reader observations of this window are not newly certified.

## v0.1.0-alpha.4 / M1030 — 2026-11-04

A complete run passed 2,318 integration + 3,429 WPF = 5,747 checks on PostgreSQL 17.11, on the same disposable loopback cluster with `pg_stat_statements` preloaded; the cluster stopped afterward. In the public checkout, the focused suite `Test-Integration.ps1 -ActivityOnly` passed 71 integration + 152 WPF checks and `-PartitionTablesOnly` passed 113 integration + 101 WPF checks. On the same application source, in the development repository, the Activity Monitor suite passed 71 integration + 152 WPF checks on PostgreSQL 16.15 and on 18.6, with matching-major tools; the Query Statistics suite passed on both versions on the M1029 source, and its code has not changed since. Integration checks cover blocking chains and the head blocker across databases, paging by PID within one snapshot, overview metrics from `pg_stat_io` and `pg_stat_database`, sampled waits that leave out `pg_sleep`, the change in `pg_stat_statements` between refreshes, concurrent detach with its preconditions and `FINALIZE`, and creating, attaching and detaching sub-partitioned tables. WPF checks cover the document tab and its reuse, panels, charts, pause/resume, refresh intervals, row and panel menus, Session Details and minimum-size layout.

The Activity Monitor was compared twice, during development, with the one in SSMS 22.11, under the same blocking workload against SQL Server LocalDB and the disposable PostgreSQL cluster, including real right clicks on the Overview menu. The differences found were fixed except these, kept by decision or by what PostgreSQL offers: double-clicking a session opens Session Details (SSMS does nothing); the selection follows the session when the list is re-sorted (SSMS keeps the position); Overview shows active sessions where SSMS shows processor time; Data File I/O is per database because PostgreSQL has no per-file statistics; Resource Waits is sampled rather than cumulative. Nothing was tested with more than 1,000 sessions, and physical DPI and screen-reader observations of the new screens are not newly certified.