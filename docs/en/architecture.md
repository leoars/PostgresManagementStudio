# Architecture

English | [Português (Brasil)](../pt-BR/architecture.md)

This describes the implemented project structure, not every module proposed in early design documents.

| Project/directory | Responsibility |
| --- | --- |
| `src/PGMS.Desktop` | WPF shell, docking, AvalonEdit, grids, dialogs, connection profiles, files and local recovery. |
| `src/PGMS.PostgreSql` | Npgsql sessions, catalog inspection, execution, typed administrative edits and external PostgreSQL tools. |
| `tests/PGMS.IntegrationTests` | Real-server behavior with disposable synthetic databases in the project cluster. |
| `tests/PGMS.DesktopTests` | WPF contracts, files/recovery, UI Automation and installed-child probes. |
| `scripts` | Build, isolated PostgreSQL test orchestration, package/install validation and evidence tools. |
| `tools/PGMS.TraceWindows` | Development profiling helper; not a distributed app dependency. |

## Boundaries

The desktop uses PostgreSQL services through a project reference. A query tab owns its session, including transaction and session settings; object inspection and administrative operations may use dedicated connections. Server roles remain the authority. Typed operations generate PostgreSQL SQL and apply operation-specific identity/concurrency checks; they are not an arbitrary DDL translator.

WPF controls and rendering belong to the UI thread. File I/O and several preparation/checkpoint steps use asynchronous work, but rendering large SQL lines can still stall the UI. Query results and recovery have explicit retention limits. Cancel is a request, not proof of rollback or guaranteed termination by a deadline.

Profiles, layout, drafts and administrative journals live in a configurable local state directory. Secrets and recovery payloads use Windows DPAPI for the current user. Recovery formats are versioned; old binaries are not universally backward-compatible. External PostgreSQL tools run as child processes with reviewed arguments and protected temporary staging where applicable.

SSMS observations guide development only. No SSMS assembly is required by the distributed application. See [licensing](licensing.md), [validation](validation.md) for the supported behavior and limits.
