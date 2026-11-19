# v0.1.0-beta.1 — Windows x64

English | [Português (Brasil)](../pt-BR/release-notes.md)

First beta of PGMS, still published as a pre-release. The features of this version are complete and it is meant for real work; it can still have bugs, so keep backups and report what you find. Build label: M1143. The binaries are built and attested by the same [release workflow](../../.github/workflows/release.yml) as the previous releases. Original work is under Apache-2.0, with credits in `NOTICE`; releases through alpha.4 remain under MIT.

## Highlights

- **Interface in 8 languages.** English, Português (Brasil), Español, Deutsch, Italiano, Français, 한국어 and 日本語. PGMS follows the Windows display language by default (English when Windows uses another one); **Tools ▸ Options ▸ Environment ▸ International Settings** chooses another, applied when PGMS starts again. Object Explorer folder names and the command names of the Keyboard page stay in English, as in SSMS; SQL, data and server messages are never translated. **The translations have not been reviewed by native speakers yet**: please [report any translation problem](https://github.com/leoars/PostgresManagementStudio/issues/new?template=translation.yml).
- **AI assistant** (unchanged since alpha.7): **Query ▸ Generate SQL with AI…** (`Ctrl+I`) with follow-up requests, and **Explain**, **Fix** and **Optimize SQL with AI**. It works with Anthropic, OpenAI or a local Ollama, with your own key. The request, the database structure (names, types, keys, comments) and, for **Fix**, the SQLSTATE and main message of the last error go to the provider you set; table data is never read or sent, nothing is sent until you press **Generate**, and the SQL is never run for you.
- **Configure Server TLS** (unchanged): a guided setup that installs a certificate and turns on TLS, restoring the previous settings if it fails. It requires a superuser connection to a server running on Windows.
- **Themes as in SSMS.** **Tools ▸ Theme** lists the 13 SSMS themes in the SSMS order (Cool Breeze, the look PGMS always had, is the default; Light is the SSMS gray; Dark; and the color themes) plus **Use system setting**.
- **Execution plans.** The plan menu adds **Show Execution Plan JSON**, **Show Execution Plan as Text** (the `EXPLAIN` text layout, built without running the query again) and **Compare Showplan**, which compares the plan with a saved plan file or with the previous plan of the same tab. Operators that differ are shaded in both drawings (amber: other values; pink: only in one plan), with a Differences grid.
- **PostGIS.** `geometry` and `geography` values appear as EWKT text (`SRID=4326;POINT(1 2)`) in the grid, Results to Text, copy and export; Table Designer accepts types such as `geometry(Point,4326)` and lists the types of installed extensions.
- **Update notice.** A few seconds after start, release packages ask the GitHub API whether a newer release exists (GitHub sees your IP address, as with any web request) and show **Update available** in the title bar, with Open the release page and Skip This Version. **Help ▸ Check for Updates** asks at once. Nothing is downloaded or installed. Turn it off in **Tools ▸ Options ▸ Environment ▸ Product Updates**.

## What changed since v0.1.0-alpha.7

**Options**
- New pages, each option with a real effect or shown disabled with the reason: Environment ▸ **AutoRecover** (on/off, interval in minutes, lifetime in days), **Documents** (files changed outside PGMS, read-only files, Open from the folder of the active file, mixed line endings, save in the background, save encoding), **Tabs**, **Task List** (read only) and **Windows**; the seven **SQL Formatter** pages (keyword casing, Format on save, alignment, indentation, multiline lists, new lines, spacing); Designers ▸ **Table Options** (save time-out, warning on a table without a primary key); Object Explorer ▸ **General** (Hide system objects) and **Commands** (confirm renames); Query Execution ▸ **Live Query Statistics** (polling interval); Text Editor ▸ **Transact-SQL** (word wrap, line numbers, tabs, IntelliSense for new windows).
- **Defaults that follow SSMS:** new tabs open first, after the pinned tabs; unsaved queries are backed up every 5 minutes; system schemas (`pg_catalog`, `information_schema`) and template databases are listed in Object Explorer; a Table Designer statement that runs longer than 3600 seconds stops the save and nothing is changed; Live Query Statistics reads every 250 ms; Format SQL writes a space between a type and its parameters (`VARCHAR (20)`). Recovered queries still come back in their saved order. Change any of these back in Tools ▸ Options.
- **Results to Grid ▸ Show multiple result sets stacked in one tab** (off by default: one tab per result set).

**Editor and windows**
- **Edit ▸ Paste Special** ▸ Format SQL (pastes formatted, one Undo) and Paste with AI…; **Edit ▸ IntelliSense ▸ Switch Completion Mode** (`Ctrl+Alt+Space`); **File ▸ New ▸ File…** (`Ctrl+Alt+N`); **Tools ▸ Code Snippets Manager…** (`Ctrl+K`, `Ctrl+B`).
- **View ▸ Navigate Backward/Forward** (`Ctrl+-`, `Ctrl+Shift+-`), **All Windows** in Full Screen (`Shift+Alt+M`), **Window ▸ Pin Tab** (pinned tabs stay pinned when recovered or reopened with Restore Closed Tab) and Reset Current Object Explorer Details View; the Standard and SQL Editor toolbars have the SSMS buttons in the SSMS order.
- Message boxes are drawn by PGMS, with the buttons in the PGMS language, the usual keys (Enter, Esc, access letters), the system icon and sound, and `Ctrl+C` to copy the text.
- Tab titles and composed messages are translated; file and query names are not.

**Performance and accessibility**
- Result grids draw each cell with one element and expose fewer automation objects: scrolling and loading large results are faster. The grid fills in the same steps as SSMS (30, 100, 10,000 rows, then the end).
- When PGMS reopens after an unexpected close, only the active recovered tab builds its editor at once; the others build when shown.
- Keyboard and screen reader fixes from a pass with real key presses: tool window focus and size, Tab no longer stuck in toolbars (toolbars are out of the Tab order, as in SSMS), auto-hidden panes and splitters out of the Tab order, names read by Narrator in Connect and in editable combo boxes. Every window now fits the screen's work area, so dialogs are no longer cut at 175% scaling. Tabs, Error List, Output and Schema Compare follow the dark theme.

## Downloads

From [this release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-beta.1):

| File | Purpose |
| --- | --- |
| `PGMS-0.1.0-beta.1-Setup-x64.exe` | Graphical installer (English/pt-BR). Current user without administrator rights, or all users with elevation. No PowerShell or separate .NET runtime required. |
| `PGMS-0.1.0-beta.1-win-x64.zip` | Portable application with .NET/WPF runtime. Extract the entire folder and run PGMS.exe. Includes the managed PowerShell 7 scripts. |
| `SHA256SUMS.txt` | SHA256 of every asset in this release. Hashes check correspondence, not authorship. |
| `package-manifest.json` | SHA256 manifest of every file inside the ZIP, as checked by `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | These notes. |

The installer updates an existing installation in place; profiles, drafts, Query History and settings live in `%LOCALAPPDATA%\PGMS` and are not removed. An update over alpha.7 on a clean machine is not certified yet (see [Validation](#validation)).

## Verify what you downloaded

The binaries are not code-signed, so Windows SmartScreen may warn on first launch. Before choosing **Run anyway**, confirm the checksum and the provenance:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-beta.1-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare the hashes with `SHA256SUMS.txt`. The second command needs the [GitHub CLI](https://cli.github.com/) and confirms that the file was produced by this repository's release workflow at the tagged commit; run it for the ZIP as well. Code signing through the SignPath Foundation open source program is planned; no date is promised.

## Report a problem

When PGMS shows the unexpected error window, choose **Report Issue** and attach the log from **Open Log Folder** (`%LOCALAPPDATA%\PGMS\logs`). For any other problem, [open a bug report](https://github.com/leoars/PostgresManagementStudio/issues/new?template=bug_report.yml): it asks for the PGMS version (Help ▸ About), the Windows and PostgreSQL versions, the installation method, the interface language, the steps and the log when there is one. Read the log before attaching it: an error message can quote what the server sent. Translation problems have [their own form](https://github.com/leoars/PostgresManagementStudio/issues/new?template=translation.yml).

## Scope

SQL editor with IntelliSense and independent sessions, Object Explorer, paged Results and Results to Text/File, CSV/JSON export, database diagrams and designers, graphical, text and JSON plans with comparison, reviewed PostgreSQL administration, identity/privileges, Activity Monitor, query statistics, Query History, Schema Compare, logical backup/restore and the AI assistant. PostgreSQL 16 to 18 and same-major client tools are required for database operations and backup/restore; server/tools are not bundled. SSMS is neither required nor distributed.

## Validation

Application behavior: the M1143 source passed the complete regression of this repository on PostgreSQL 17.11, and the M1141 source (the same features without the six default changes of M1143) passed it on 16.15, 17.11 and 18.6 (see [validation](validation.md#v010-beta1--m1143--2026-11-19)), on an isolated loopback cluster; the host PostgreSQL service is not used. Keyboard-only navigation with real key presses, Narrator, 100/150/175% scaling in three themes and three languages, and the performance scenarios of the project were checked on the maintainer's machine before the default changes. PostGIS was tested with the official PostGIS 3.7.0 package on PostgreSQL 17.11. The AI features were tested with a local fake provider, not with a real one.

A clean-Windows VM installation and another Windows user without privileges are not certified by this release.

## Known limits

Windows x64 only. Unsigned executable and installer; SmartScreen warns. No MSI, winget or automatic update (only the update notice). Full SSMS parity is not claimed. Translations without native review. One caret only (no multiple carets). Live Query Statistics has no live plan, because PostgreSQL has no per-operator counters while a query runs; there is no plan Analyze. OpenAI was not tested with a real key. Configure Server TLS needs a Windows server and a superuser. Running a 10,000 × 20 query with five tabs open can pause the interface for about 0.4 s, and reopening five recovered drafts takes about 2 s. With system objects shown, `template0` appears Offline and cannot be expanded (it does not accept connections). Results: sorting, copy and export apply to the current page; rows after the first page stay in a temporary file, unencrypted, until the next execution or until the tab closes. IntelliSense does not know CTE columns, temporary tables or `SET search_path` of the session. Query Designer: `UNION` and `WITH` open only in the SQL pane. The error log masks passwords only in `key=value` form.

## Previous releases

v0.1.0-alpha.7 (2026-11-17, AI conversation, dark theme, error log), v0.1.0-alpha.6 (2026-11-13, diagram indexes and descriptions, Query History, Script as, plan warnings) and v0.1.0-alpha.5 (2026-11-08, database diagrams, Query Designer, IntelliSense; Apache-2.0) remain available with their original assets and notes. v0.1.0-alpha.4 (2026-11-07), v0.1.0-alpha.3 (2026-11-02), v0.1.0-alpha.2 (2026-11-01, first CI-built release with attestation) and v0.1.0-alpha.1 (2026-10-31) remain available with their original assets and notes, under MIT.
