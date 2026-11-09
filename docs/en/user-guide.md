# User guide

English | [Português (Brasil)](../pt-BR/user-guide.md)

## Connect and query

Open **New Query** (`Ctrl+N`), then **Connect**. Enter host, port, database, role and TLS settings. The default VerifyFull requires a trusted certificate and matching hostname. **Test Connection** checks the connection without saving a profile. Save a profile explicitly; password storage is optional and protected by Windows DPAPI. Each query tab owns its own PostgreSQL session. Switching database needs a connection, not a SQL Server `USE` command. T-SQL is not translated.

Use `F5` to execute the selection or document and **Cancel** to request cancellation. Cancellation does not prove that a server-side command or COMMIT was rolled back. Inspect the result before repeating a write. Results/Messages show results and diagnostics. Read timeout, server session settings and explain plans have dedicated controls. **Analyze Plan** executes SQL and requires review; use it only when that execution is intended. SQL with `$1`-style placeholders cannot run without parameter values: PGMS refuses it before sending, keeps the session open and points to the placeholder. `PREPARE` and function/procedure bodies are exempt, and **Estimated Plan** switches to a generic plan.

Results are bounded: 10,000 rows, 100 result sets, 256 columns per result, 4,096 columns total and 250,000 cells, with additional text limits. Warnings explain omitted data; later SQL can still execute. CSV/JSON export covers retained displayed/selected data, not a full database export. `Ctrl+C` copies with headers; `Ctrl+Shift+C` omits headers. Copy is limited to 250,000 positions/4Mi UTF-16 units and distinguishes NULL from empty text.

## Inspect and administer

Object Explorer loads folders on demand, filters and inspects objects. Supported operations offer SQL preview and explicit Apply. Script output does not include all concurrent-change protections of Apply. Use the least PostgreSQL privileges required; PGMS does not bypass server authorization. Check target database/schema/object and role before applying.

Identity and sequence operations distinguish configured START from RESTART. A restart can repeat existing values. The **Partitions** dialog creates, attaches and detaches partitions. A new child can itself be partitioned (**Sub-partition by**, keyed by columns of the parent), and a partitioned table can be attached or detached with its sub-tree. **Detach concurrently** runs `DETACH PARTITION … CONCURRENTLY` outside a transaction, without blocking queries; it is refused for a DEFAULT partition or while another detach is pending, and a pending detach is finished with `FINALIZE`. Privilege changes, delegation, CASCADE and COMMIT reconciliation have specific review and concurrency rules. See the [documentation index](../README.md) for supported behavior.

## Database diagrams

In Object Explorer, right-click **Database Diagrams** under a database and choose **New Database Diagram**. The first time, PGMS asks to create its support objects (schema `pgms`). Add tables with **Add Table** or **Add Related Tables**, drag a column onto another to create a foreign key, and use **Arrange Tables** to lay them out. Saving shows the SQL script of any change to the database for review.

## Query Designer

**Query ▸ Design Query in Editor** (`Ctrl+Shift+Q`) opens the statement under the cursor, or the selection, in the Diagram, Criteria and SQL panes. **OK** replaces that statement in the editor; nothing is executed. On an empty line it starts empty and inserts at the cursor. `UNION` and `WITH` open only in the SQL pane.

## IntelliSense

`Ctrl+Space` or `Ctrl+J` opens the list; typing a dot opens it by itself. `Tab` accepts and `Esc` closes. **Query ▸ IntelliSense Enabled** (`Ctrl+B`, `Ctrl+I`) turns it off for the tab, and **Edit ▸ IntelliSense ▸ Refresh Local Cache** (`Ctrl+Shift+R`) reads the catalog again after schema changes. After `FROM` and similar keywords it searches table names in every user schema; after `JOIN` it offers whole joins from foreign keys.

## Large results

A grid result larger than **Query Options ▸ Results ▸ Grid ▸ Rows per result page** (100,000 by default) is shown in pages, with a bar below the grid. Rows after the first page are kept in a temporary file until the next execution or until the tab closes. Sorting, copy and export apply to the current page; **Results to File** writes every row.

## Activity Monitor

**View → Activity Monitor** (`Ctrl+Alt+A`) opens a document tab for the connected server and role (`host:port (role) - Activity Monitor`); opening it again activates the same tab. It has six collapsible panels, following the SSMS layout:

- **Overview**: charts of active sessions, waiting sessions, database I/O (`pg_stat_io`) and transactions per second. Its context menu sets the refresh interval and pauses, resumes or refreshes the whole monitor.
- **Processes**: sessions from `pg_stat_activity` with host, state, query and transaction age, wait, **Blocked by PID** and **Head blocker**, filters applied on the server and pages by PID. **Details…** opens the session's most recent query; **Cancel Query** and **Terminate Session…** act on the selected session.
- **Resource Waits**: wait categories sampled at each refresh. PostgreSQL keeps no cumulative wait time, so these are estimates; idle waits and `pg_sleep` are left out.
- **Data File I/O**: reads, temporary writes, read time and buffer hit per database (`pg_stat_database`), not per file: PostgreSQL has no per-file statistics.
- **Recent Expensive Queries**: the change in `pg_stat_statements` between refreshes; requires the extension, as Query Statistics does.
- **Active Expensive Queries**: statements running now. **Edit Query Text** opens one in a query tab; **Show Execution Plan** shows an estimated plan without executing it.

Auto-refresh starts on, every 10 seconds, with intervals from 1 second to 1 hour. **Cancel Refresh**, **Cancel Query** and **Terminate Session** are distinct actions; refreshing a snapshot is not authorization to terminate a session. Pages are separate reads, so a session can move between pages, and sorting applies within the loaded page. Roles outside `pg_read_all_stats` may not see other roles' queries and waits.

## Query statistics

**View → Query Statistics…** reads `pg_stat_statements` for the connected database: normalized statements with calls, execution time (total, mean, minimum, maximum, standard deviation), rows, cache hit %, shared and temporary blocks, I/O time and WAL bytes. The purpose is similar to SQL Server Query Store; it is not a copy of its screens or reports.

PGMS never loads, creates or configures the extension. The server needs `shared_preload_libraries = 'pg_stat_statements'` (a restart) and `CREATE EXTENSION pg_stat_statements` in a database; when something is missing, a yellow band says what. Roles outside `pg_read_all_stats` see other users' statements without SQL text, as PostgreSQL decides. I/O time is only recorded with `track_io_timing` on. PostgreSQL has no per-statement CPU time. Extension version 1.10 (PostgreSQL 16) is read without `stats_since`: a statement removed and tracked again during a time window can count low, and the window status says so. Versions older than 1.10 are not read; the band names the `ALTER EXTENSION pg_stat_statements UPDATE` a privileged user can run, and PGMS changes nothing.

Filters (database, user, SQL text, minimum calls) and ordering run on the server before a 1,000-row limit. Clicking the Total, Mean, Max, Calls, Rows or metric headers reloads in that order; other headers sort only the loaded rows. **Window** shows cumulative values since the last reset, values since a saved baseline, or the last 5/15/60 minutes, computed by subtracting an earlier reading; sliding windows sample only while the window is open, and minimum/maximum are not available for a window. A top-N chart beside the grid follows the applied order. **Show Plan** opens the selected statement in a new query tab with `EXPLAIN (GENERIC_PLAN)`, without executing it or binding `$n` values. **Reset Selected** and **Reset All** call `pg_stat_statements_reset` after a confirmation whose default is No; the server decides whether the role may reset. Auto-refresh is optional and starts off. Filters, chart choices and baselines are stored per connection target in the PGMS state folder, without passwords or SQL results.

## Backup and restore

Choose compatible PostgreSQL tools and a new output file/directory. Custom, tar, plain SQL and directory formats support the documented content choices. Restore uses an existing target database; compatible objects/roles and permissions must already exist where required. Ownership and privileges are explicit choices. Directory backup supports parallel dump; restore remains serial. Global operations cover roles/memberships/tablespaces, not a physical cluster backup.

Only run trusted SQL scripts: psql scripts may include local commands, reconnects and COMMIT. Single-transaction options cannot override those commands. Cancellation can arrive after a commit. Encrypted client keys, CR/LF in tool passwords and non-ASCII TEMP paths have documented restrictions. Keep the source backup unchanged and inspect the destination after failures. The [distribution guide](distribution.md) describes tools, permissions and package requirements.

## Save and recover

Save SQL files explicitly. Background recovery stores bounded encrypted drafts and may reopen them disconnected after interruption. Review recovered tabs and use **Save As**. This is not a backup system or proof that every latest keystroke was persisted. Conflicting external file changes are refused. Close the app before backing up local state; DPAPI is tied to the Windows user. Builds earlier than M934 cannot read the newer recovery container. See [validation](validation.md) for the exact cross-version recovery tested.
