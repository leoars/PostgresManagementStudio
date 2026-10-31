# User guide

English | [Português (Brasil)](../pt-BR/user-guide.md)

## Connect and query

Open **New Query** (`Ctrl+N`), then **Connect**. Enter host, port, database, role and TLS settings. The default VerifyFull requires a trusted certificate and matching hostname. **Test Connection** checks the connection without saving a profile. Save a profile explicitly; password storage is optional and protected by Windows DPAPI. Each query tab owns its own PostgreSQL session. Switching database needs a connection, not a SQL Server `USE` command. T-SQL is not translated.

Use `F5` to execute the selection or document and **Cancel** to request cancellation. Cancellation does not prove that a server-side command or COMMIT was rolled back. Inspect the result before repeating a write. Results/Messages show results and diagnostics. Read timeout, server session settings and explain plans have dedicated controls. **Analyze Plan** executes SQL and requires review; use it only when that execution is intended.

Results are bounded: 10,000 rows, 100 result sets, 256 columns per result, 4,096 columns total and 250,000 cells, with additional text limits. Warnings explain omitted data; later SQL can still execute. CSV/JSON export covers retained displayed/selected data, not a full database export. `Ctrl+C` copies with headers; `Ctrl+Shift+C` omits headers. Copy is limited to 250,000 positions/4Mi UTF-16 units and distinguishes NULL from empty text.

## Inspect and administer

Object Explorer loads folders on demand, filters and inspects objects. Supported operations offer SQL preview and explicit Apply. Script output does not include all concurrent-change protections of Apply. Use the least PostgreSQL privileges required; PGMS does not bypass server authorization. Check target database/schema/object and role before applying.

Identity and sequence operations distinguish configured START from RESTART. A restart can repeat existing values. Privilege changes, delegation, CASCADE and COMMIT reconciliation have specific review and concurrency rules. Activity Monitor distinguishes **Cancel Refresh**, **Cancel Query** and **Terminate Session**. Refreshing a snapshot is not authorization to terminate a session. See the [documentation index](../README.md) for supported behavior.

## Backup and restore

Choose compatible PostgreSQL tools and a new output file/directory. Custom, tar, plain SQL and directory formats support the documented content choices. Restore uses an existing target database; compatible objects/roles and permissions must already exist where required. Ownership and privileges are explicit choices. Directory backup supports parallel dump; restore remains serial. Global operations cover roles/memberships/tablespaces, not a physical cluster backup.

Only run trusted SQL scripts: psql scripts may include local commands, reconnects and COMMIT. Single-transaction options cannot override those commands. Cancellation can arrive after a commit. Encrypted client keys, CR/LF in tool passwords and non-ASCII TEMP paths have documented restrictions. Keep the source backup unchanged and inspect the destination after failures. The [distribution guide](distribution.md) describes tools, permissions and package requirements.

## Save and recover

Save SQL files explicitly. Background recovery stores bounded encrypted drafts and may reopen them disconnected after interruption. Review recovered tabs and use **Save As**. This is not a backup system or proof that every latest keystroke was persisted. Conflicting external file changes are refused. Close the app before backing up local state; DPAPI is tied to the Windows user. Builds earlier than M934 cannot read the newer recovery container. See [validation](validation.md) for the exact cross-version recovery tested.
