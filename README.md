# PGMS — Postgres Management Studio

English | [Português (Brasil)](README.pt-BR.md)

[![Build](https://github.com/leoars/PostgresManagementStudio/actions/workflows/build.yml/badge.svg)](https://github.com/leoars/PostgresManagementStudio/actions/workflows/build.yml) [![Pre-release](https://img.shields.io/github/v/release/leoars/PostgresManagementStudio?include_prereleases&label=pre-release)](https://github.com/leoars/PostgresManagementStudio/releases) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**The SQL Server Management Studio experience, for PostgreSQL.** A native Windows client with Object Explorer, F5 to run, execution plans, session monitoring and backup/restore, built for people who already think in SSMS.

**[Download v0.1.0-alpha.1 for Windows x64](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.1)** · Graphical installer (EN/pt-BR) or portable ZIP, .NET runtime included · Works with PostgreSQL 17 and 18

![PGMS editor, Object Explorer and query results](docs/assets/pgms-results-m959.jpg)

> Early pre-release. Querying, browsing and monitoring are the core of this alpha; administration and backup run through reviewable SQL and the official PostgreSQL tools. Full SSMS parity is not claimed. See [known limits](docs/en/validation.md) and [release notes](docs/en/release-notes.md).

## Why PGMS

- **Feels like SSMS.** Object Explorer, query tabs with their own sessions, Results/Messages, F5 and the shortcuts you already use.
- **Review before you apply.** Administrative changes are shown as SQL first, so nothing touches the server without you reading it.
- **Know what's running.** Session and blocker monitor that distinguishes cancelling a query from terminating a session.
- **Native and fast.** C#/WPF on .NET 10, no browser and no JVM.
- **Your drafts survive.** Unsaved query tabs are checkpointed and recovered after a crash or forced close, encrypted per Windows user.

## Features

- SQL editor: highlighting, find/replace, multiple tabs, save/load and encrypted draft recovery.
- Independent PostgreSQL sessions, execute/cancel, Results/Messages, bounded result grids, copy and CSV/JSON export.
- Object Explorer, inspection, estimated/analyzed query plans and session configuration.
- Reviewed SQL administration for supported databases, schemas, tables, columns, identity, indexes, constraints, sequences, roles and privileges.
- Session/blocker monitoring, with distinct query cancellation and session termination.
- Logical backup/restore through external PostgreSQL tools: custom, tar, SQL and directory formats, plus reviewed global operations.

## Get started

### Install

Download the installer or the ZIP from the [latest release](https://github.com/leoars/PostgresManagementStudio/releases), run it, then use **Connect** and **New Query**. Backup/restore needs the PostgreSQL client tools (`pg_dump`, `pg_restore`, `psql`) of the same major version as the server. Details: [setup guide](docs/en/getting-started.md).

#### Windows SmartScreen warning

PGMS releases are not code-signed yet, so Windows may show "Windows protected your PC" the first time you run the installer or `PGMS.exe`. Choose **More info** and then **Run anyway**. Before doing so, confirm the download matches the checksums published with the release:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
```

Compare the hashes with `SHA256SUMS.txt` and the installer's `.sha256` file in the release. v0.1.0-alpha.1 was built on the maintainer's machine, so it has checksums but no build provenance attestation; releases produced by the [release workflow](.github/workflows/release.yml) can additionally be verified with `gh attestation verify`. Code signing through the SignPath Foundation open source program is planned.

### Build from source

For development: Windows x64, PowerShell 7 and the .NET SDK selected by [global.json](global.json) (`10.0.400`, latest installed patch in that feature band). PostgreSQL 17/18 and matching-major tools are needed for database tests and backup/restore.

```powershell
git clone https://github.com/leoars/PostgresManagementStudio.git
cd PostgresManagementStudio
./scripts/Build.ps1
dotnet run --project src/PGMS.Desktop
```

The self-contained portable package includes .NET/WPF, but no PostgreSQL server or tools. Local candidate ZIPs and acceptance artifacts are absent from a fresh clone. See [setup and packaging](docs/en/getting-started.md).

## Documentation

| Topic | English | Português (Brasil) |
| --- | --- | --- |
| Setup and packaging | [Guide](docs/en/getting-started.md) | [Guia](docs/pt-BR/getting-started.md) |
| Queries, administration, backup | [User guide](docs/en/user-guide.md) | [Guia de uso](docs/pt-BR/user-guide.md) |
| Build, tests, contributions | [Development](docs/en/development.md) | [Desenvolvimento](docs/pt-BR/development.md) |
| Structure | [Architecture](docs/en/architecture.md) | [Arquitetura](docs/pt-BR/architecture.md) |
| Evidence and limits | [Validation](docs/en/validation.md) | [Validação](docs/pt-BR/validation.md) |
| Attribution | [Licensing](docs/en/licensing.md) | [Licenças](docs/pt-BR/licensing.md) |

[Documentation index](docs/README.md). Public guides are maintained in both languages; the original internal development history is not included in this curated source repository. Bilingual documentation does not localize the application UI.

## Contribute and report problems

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [code of conduct](CODE_OF_CONDUCT.md). English and Brazilian Portuguese are welcome. Issue templates guide bug reports and feature requests; usage questions go to [Discussions](https://github.com/leoars/PostgresManagementStudio/discussions). Use synthetic data and remove credentials and personal information from reports. For vulnerabilities, follow [SECURITY.md](SECURITY.md).

## License

PGMS's original code and documentation use [MIT](LICENSE). Third-party components and the PostgreSQL elephant retain their own licenses and trademark terms; see [third-party notices](THIRD-PARTY-NOTICES.md). PGMS is independent, without endorsement by Microsoft or the PostgreSQL project. SSMS is a reference, not a distributed dependency.
