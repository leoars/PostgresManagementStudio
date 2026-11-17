# v0.1.0-alpha.7 — Windows x64

English | [Português (Brasil)](../pt-BR/release-notes.md)

Seventh public pre-release of PGMS. It is an alpha for evaluation with synthetic/disposable data. Build label: M1117. The binaries are built and attested by the same [release workflow](../../.github/workflows/release.yml) as the previous releases. Original work is under Apache-2.0, with credits in `NOTICE`; releases through alpha.4 remain under MIT.

## Highlights

- **AI assistant: conversation and Explain/Fix/Optimize.** The **Generate SQL with AI** window now takes follow-up requests: the last five requests of the conversation and the SQL written for them go with the next one, and **New Conversation** starts over. **Query ▸ Explain SQL with AI**, **Fix SQL with AI** and **Optimize SQL with AI** open the window with the selected SQL (or the document) and a request already written; **Fix** adds the SQLSTATE and the main message of the last error of the tab, but never its DETAIL, which can quote row values. Nothing is sent until you press **Generate**, and nothing is executed: the SQL only goes into the editor. The database structure goes to the provider you set (Anthropic, OpenAI or a local Ollama); table data is never read or sent. With Ollama, PGMS now asks for a context window that fits the prompt and trims the structure to the model's maximum, instead of letting the server cut the prompt silently.
- **Configure Server TLS** is unchanged: a guided setup that installs a certificate and turns on TLS, restoring the previous settings if it fails. It requires a superuser connection to a server running on Windows.
- **Unexpected errors are logged.** An error that PGMS did not handle no longer closes the program silently. It is written to a daily log in `%LOCALAPPDATA%\PGMS\logs` (kept 30 days; password values in `key=value` text are masked) and a window shows the error with **Copy Details**, **Open Log Folder**, **Report Issue**, **Continue** (the default) and **Close PGMS**. Nothing is sent over the network.
- **Dark theme.** **Tools ▸ Theme** offers Light, Dark and Use system setting. In the dark theme the shell, menus, standard controls, result grids, the tree and the Properties window follow it; database diagrams and graphical plans stay light, like paper. **Tools ▸ Editor Appearance** sets the editor colors on its own (including two extra-contrast modes) and **View ▸ Presenter Mode** (`Ctrl+Shift+F5`) enlarges the editor, results and window text for a presentation.

## What changed since v0.1.0-alpha.6

**Editor**
- **Edit ▸ Advanced:** toggle line and block comments, upper/lower case, move, sort, join and duplicate lines, delete horizontal white space, tabify/untabify, indentation, Insert File As Text, Expand Selection, Containing Block, Expand to Line, next/previous subword, Incremental Search, **Format SQL** (`Ctrl+K`, `Ctrl+Q`; only spaces, line breaks and keyword case change) and **Toggle Spell Checker** (comments and strings, through the Windows spell checker).
- An editor information bar with zoom (20–400 %, `Ctrl`+wheel), line and column (opens Go To Line), tabs/spaces, line ending (CRLF/LF/CR) and encoding, with **Set End of Line Sequence**, **Set Indentation** and **Set Encoding** (Unicode encodings).
- **IntelliSense:** Parameter Info (`Ctrl+Shift+Space`) and Quick Info (`Ctrl+K`, `Ctrl+I`, or hovering).
- **Bookmarks** per tab (`Ctrl+K`, `Ctrl+K` and the SSMS chords) and **Outlining** by statement and block comment.
- **Templates and snippets:** Template Explorer (`Ctrl+Alt+T`) with PostgreSQL templates and your own `.sql` files, Specify Values for Template Parameters (`Ctrl+Shift+M`), Insert Snippet and Surround With.
- The error position reported by the server is underlined in the editor, counted in the information bar and reached by **Go To Next/Previous Issue in File** (`Alt+PgDn`/`Alt+PgUp`).
- **Find in Files** and **Replace in Files** (`Ctrl+Shift+H`) over a folder, the open documents or the current one, with a Find Results pane.
- Clipboard history (`Ctrl+Shift+V`) and Go To Last Edit Location (`Ctrl+Shift+Backspace`).
- Lines over 10,000 characters are shown cut, with a marker that opens the whole line, so the editor no longer pauses on them.

**Files, tabs and windows**
- **File ▸ New ▸ Query with Current Connection** and **Database Engine Query**; **Open ▸ File** connects the new tab with the current connection, and **File Disconnected…** opens without connecting; **Disconnect All Queries**.
- Save All, Recent Files (count in Tools ▸ Options), Close All Tabs and **Restore Closed Tab**, also for queries without a file.
- **Window:** next/previous pane (`F6`), Show Results Pane (`Ctrl+R`), Split Tab, Dock, Auto Hide, Move To New Window, saved window layouts (up to ten) and **View ▸ Full Screen**.
- The menu now shares the title bar, with the window buttons and a **Search** box (`Ctrl+Q`) that finds menu commands and Tools ▸ Options pages and settings.
- **View ▸ Properties Window** (`F4`), **Error List** (`Ctrl+\`, `E`) with the last error of each tab, and **Output** (`Ctrl+Alt+O`) with the messages and executions of the session.
- **File ▸ Page Setup…** and **Print…** for the SQL of the active tab.
- Connection texts closer to SSMS: the server node shows `host:port (PostgreSQL version - login)` and the query status bar shows server, login with backend PID, database, time and rows.

**Queries and results**
- Results/Messages tabs are above the results, and **Save Results As…** in the grid menu opens **Save Grid Results** (`.csv` comma separated or `.txt` tab separated, with headers). The export options band is still available from **Export Options…**.
- **Query ▸ Include Live Query Statistics** shows, once a second over a separate connection, the state, elapsed time, waits, blocking sessions and `pg_stat_progress_*` progress (CREATE INDEX, VACUUM, ANALYZE, CLUSTER, COPY) of the running query. PostgreSQL has no per-operator counters, so there is no live plan.
- **Query Shortcuts** (`Alt+F1`, `Ctrl+1` to `Ctrl+0`) in Tools ▸ Options, empty by default.
- Extension types such as PostGIS `geometry` are shown as the text the server sends (hexadecimal EWKB), without errors.

**Object Explorer and administration**
- **Object Explorer Details** (`F7`), the contents of the selected node with search.
- **Keys**, **Constraints** and **Triggers**: Modify, Rename, Delete with the exact command (no CASCADE), New Foreign Key… and New Constraint…; Enable/Disable of triggers; New Function…, New Stored Procedure… and New Trigger… from templates.
- An **Extensions** folder per database with New Extension…, Update Extension… and Delete.
- A **Maintenance** submenu with Vacuum…, Analyze… and Refresh Materialized View…; the command opens in a query tab, not run directly.
- Composite Types, Foreign Servers and Replication (local publications and subscriptions), read only.
- The filter gains **Does not equal**.
- **Role Memberships** lists effective memberships, direct and inherited, with the path.
- **Tools ▸ Schema Compare** compares two databases (read only) and writes a change script into a query tab connected to the target. It never runs the script.
- **Back Up:** LZ4 and Zstandard compression. **Restore:** parallel jobs (1–32) and **Choose Items…** to restore only some items of an archive.
- **Connect:** client certificate, client key and key password (stored with DPAPI); backup and restore receive the key password through a temporary private service file.

**Settings**
- Tools ▸ Options: **Fonts and Colors** (Text Editor, Grid Results, Text Results), **Keyboard** (one shortcut per menu command), Object Explorer **Scripting** options, **Designers** and **Connection Dialog** options.
- **Tools ▸ Import and Export Settings…** (a `.pgmssettings` file with the Options), **External Tools…** and **Customize…** for the toolbars.
- **Help ▸ View Help** (`Ctrl+F1`) opens the packaged guide; **Release Notes** opens the release list.

**PostgreSQL 16:** four defects found by a complete regression on PostgreSQL 16.15 were fixed, among them `MAINTAIN` being offered on servers before 17.

## Downloads

From [this release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.7):

| File | Purpose |
| --- | --- |
| `PGMS-0.1.0-alpha.7-Setup-x64.exe` | Graphical installer (English/pt-BR). Current user without administrator rights, or all users with elevation. No PowerShell or separate .NET runtime required. |
| `PGMS-0.1.0-alpha.7-win-x64.zip` | Portable application with .NET/WPF runtime. Extract the entire folder and run PGMS.exe. Includes the managed PowerShell 7 scripts. |
| `SHA256SUMS.txt` | SHA256 of every asset in this release. Hashes check correspondence, not authorship. |
| `package-manifest.json` | SHA256 manifest of every file inside the ZIP, as checked by `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | These notes. |

## Verify what you downloaded

The binaries are not code-signed, so Windows SmartScreen may warn on first launch. Before choosing **Run anyway**, confirm the checksum and the provenance:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.7-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare the hashes with `SHA256SUMS.txt`. The second command needs the [GitHub CLI](https://cli.github.com/) and confirms that the file was produced by this repository's release workflow at the tagged commit; run it for the ZIP as well. Code signing through the SignPath Foundation open source program is planned; no date is promised.

## Report a problem

When PGMS shows the unexpected error window, choose **Report Issue** and attach the log from **Open Log Folder** (`%LOCALAPPDATA%\PGMS\logs`). Say what you were doing, the PostgreSQL version and whether it happens again. Read the log before attaching it: an error message can quote what the server sent. Use the [issue templates](https://github.com/leoars/PostgresManagementStudio/issues/new/choose).

## Scope

SQL editor with IntelliSense and independent sessions, Object Explorer, paged Results and Results to Text/File, CSV/JSON export, database diagrams and designers, graphical plans, reviewed PostgreSQL administration, identity/privileges, Activity Monitor, query statistics, Schema Compare and logical backup/restore. PostgreSQL 16 to 18 and same-major client tools are required for database operations and backup/restore; server/tools are not bundled. SSMS is neither required nor distributed.

## Validation

Application behavior: the M1117 source passed the complete regression of this repository on PostgreSQL 17.11 (see [validation](validation.md#v010-alpha7--m1117--2026-11-17)) on an isolated loopback cluster; the host PostgreSQL service is not used. Most features added since alpha.6 were validated by focused automated suites while they were built; a complete regression on PostgreSQL 16.15 and 18.6 was last run on an earlier source (M1066). The new AI actions and the conversation were tested with a local fake provider only, not with a real one. PostGIS itself was not installed in the test cluster: a stand-in `geometry` type was used.

A clean-Windows VM installation, another Windows user without privileges and physical DPI/keyboard/screen-reader observations of the new screens are not certified by this release.

## Known limits

Windows x64 only. Unsigned executable and installer; SmartScreen warns. No MSI, automatic update or production-support guarantee. Full SSMS parity is not claimed. Dark theme: diagrams and plans stay light; colored themes are not available. One caret only (no multiple carets). Several result sets are shown in tabs, not stacked. Live Query Statistics has no live plan. PostGIS values are shown as hexadecimal text, without WKT, GeoJSON or a map. Results: sorting, copy and export apply to the current page; rows after the first page stay in a temporary file, unencrypted, until the next execution or until the tab closes; Results to Text keeps the 10,000-row and 250,000-cell limits, and Results to File has neither. IntelliSense does not know CTE columns, temporary tables or `SET search_path` of the session. Query Designer: `UNION` and `WITH` open only in the SQL pane. Find Missing Foreign Keys is a heuristic: a matching name is a hint, not proof. The error log masks passwords only in `key=value` form.

## Previous releases

v0.1.0-alpha.6 (2026-11-13, diagram indexes and descriptions, Query History, Script as, plan warnings) and v0.1.0-alpha.5 (2026-11-08, database diagrams, Query Designer, IntelliSense; Apache-2.0) remain available with their original assets and notes. v0.1.0-alpha.4 (2026-11-07, Activity Monitor in a tab), v0.1.0-alpha.3 (2026-11-02, Query Statistics), v0.1.0-alpha.2 (2026-11-01, first CI-built release with attestation) and v0.1.0-alpha.1 (2026-10-31) remain available with their original assets and notes, under MIT.
