# v0.1.0-alpha.5 — Windows x64

English | [Português (Brasil)](../pt-BR/release-notes.md)

Fifth public pre-release of PGMS. It is an alpha for evaluation with synthetic/disposable data. Build label: M1053. The binaries are built and attested by the same [release workflow](../../.github/workflows/release.yml) as the previous releases. **License change:** from this release on, original work is under Apache-2.0, with credits in `NOTICE`; releases through alpha.4 remain under MIT.

## In development (not yet released)

These changes are on `main` and will be part of the next pre-release; the downloads below are still v0.1.0-alpha.5.

- **Indexes in database diagrams.** Each table of a diagram has an **Indexes** section, closed by default, with one row per index when opened: method (GIN, GiST, SP-GiST, BRIN, Hash), key columns or expressions, operator class when not the default (such as `gin_trgm_ops`), `INCLUDE`, UNIQUE, EXCLUDE, partial and invalid, with the predicate and the full `CREATE INDEX` in the tooltip. A warning marks a table with a foreign key that no index serves and opens **Find Unindexed Foreign Keys…**, which also arrives in this change: on a database in Object Explorer it lists such keys from the catalog and creates the indexes from a reviewed script, concurrently or in one transaction. **Show Indexes** and **Refresh** are in the diagram menu. Each save writes the indexes of each table into the diagram definition (format 2) for tools and AI agents that read it; older diagrams open unchanged. Read only: indexes are still created in **Indexes/Keys…**. SSMS 22.11 shows no indexes in a diagram. Suggested by a PGMS user.

## What changed since v0.1.0-alpha.4

- **Database diagrams.** SSMS-style diagrams saved on the server: add tables and related tables, drag a column to create a foreign key, Arrange Tables, zoom, table views, annotations, new tables and column editing inside the diagram. Database changes are shown as a SQL script first.
- **Query Designer.** **Query ▸ Design Query in Editor** (`Ctrl+Shift+Q`) with synchronized Diagram, Criteria and SQL panes, joins from foreign keys and Change Type (Update, Delete, Insert Values, Insert Results, Make Table). It writes SQL to the editor and never runs it.
- **IntelliSense.** `Ctrl+Space` and `Ctrl+J` offer what fits the position: tables, views and table functions after `FROM`, searched across all user schemas; whole joins from foreign keys after `JOIN`; columns by alias. Typing ignores accents.
- **Table Designer and View Designer** in document tabs, with a reviewed change script.
- **Graphical execution plans.** Estimated and actual plans drawn with operators and proportional arrows, properties, Find Node, zoom, and opening a saved plan.
- **Faster results.** The thread reading the server no longer waits for the screen, in the grid and in Results to Text. Grids are no longer cut at 10,000 rows or 250,000 cells: larger results are shown in pages of 100,000 rows, configurable in Query Options.
- **Results to Text and to File, Query Options and the Query menu:** per-tab options with defaults in Tools ▸ Options, Parse, Client Statistics and Connection commands.
- **Cancel reaches the server** even while a later statement of the script is running, and no longer risks dropping the connection in the middle of reading.
- **Generate SQL with AI** with Anthropic, OpenAI or a local Ollama, using your own key. Your request and the database structure go to the provider you configure; table data is never read or sent.
- **Configure Server TLS.** A guided setup that installs a certificate and turns on TLS, restoring the previous settings if it fails. Requires a superuser connection to a server running on Windows.
- **Object Explorer and administration.** Standard Reports, Registered Servers, local service Start/Stop/Restart, Edit Top and Select Top rows, Find Missing Foreign Keys, and context menus by node type.
- **About** now shows the author and links to the project.

## Downloads

From [this release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.5):

| File | Purpose |
| --- | --- |
| `PGMS-0.1.0-alpha.5-Setup-x64.exe` | Graphical installer (English/pt-BR). Current user without administrator rights, or all users with elevation. No PowerShell or separate .NET runtime required. |
| `PGMS-0.1.0-alpha.5-win-x64.zip` | Portable application with .NET/WPF runtime. Extract the entire folder and run PGMS.exe. Includes the managed PowerShell 7 scripts. |
| `SHA256SUMS.txt` | SHA256 of every asset in this release. Hashes check correspondence, not authorship. |
| `package-manifest.json` | SHA256 manifest of every file inside the ZIP, as checked by `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | These notes. |

## Verify what you downloaded

The binaries are not code-signed, so Windows SmartScreen may warn on first launch. Before choosing **Run anyway**, confirm the checksum and the provenance:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.5-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare the hashes with `SHA256SUMS.txt`. The second command needs the [GitHub CLI](https://cli.github.com/) and confirms that the file was produced by this repository's release workflow at the tagged commit; run it for the ZIP as well. Code signing through the SignPath Foundation open source program is planned; no date is promised.

## Scope

SQL editor with IntelliSense and independent sessions, Object Explorer, paged Results and Results to Text/File, CSV/JSON export, database diagrams and designers, graphical plans, reviewed PostgreSQL administration, identity/privileges, Activity Monitor, query statistics and logical backup/restore. PostgreSQL 16 to 18 and same-major client tools are required for database operations and backup/restore; server/tools are not bundled. SSMS is neither required nor distributed.

## Validation

Application behavior: the M1053 source passed the complete regression of this repository on PostgreSQL 17.11 (**2,834 integration + 4,337 WPF = 7,171 checks**) on an isolated loopback cluster; the host PostgreSQL service is not used. The features added since alpha.4 were not run as a complete regression on PostgreSQL 16 or 18.

All-users (elevated) installation, a clean-Windows VM installation and physical DPI/keyboard/screen-reader observations of the new screens are not newly certified by this release. New screens were exercised by automated tests, not by hand with mouse and keyboard on this source.

## Known limits

Windows x64 only. Unsigned executable and installer; SmartScreen warns. No MSI, automatic update or production-support guarantee. Full SSMS parity is not claimed. Results: sorting, copy and export apply to the current page; rows after the first page stay in a temporary file, unencrypted, until the next execution or until the tab closes; Results to Text keeps the 10,000-row and 250,000-cell limits, and Results to File has neither. IntelliSense has no Parameter Info or Quick Info, and does not know CTE columns, temporary tables or `SET search_path` of the session. Query Designer: `UNION` and `WITH` open only in the SQL pane; Update, Delete and Insert Values take one table. Find Missing Foreign Keys is a heuristic: a matching name is a hint, not proof.

## Previous releases

v0.1.0-alpha.4 (2026-11-07, Activity Monitor in a tab), v0.1.0-alpha.3 (2026-11-02, Query Statistics), v0.1.0-alpha.2 (2026-11-01, first CI-built release with attestation) and v0.1.0-alpha.1 (2026-10-31) remain available with their original assets and notes, under MIT.
