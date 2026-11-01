# PGMS v0.1.0-alpha.2 — portable distribution (M1008)

English | Português (Brasil): LEIA-ME.md in the package, or docs/pt-BR/distribution.md in the source repository.

Extract the entire directory and run PGMS.exe. A win-x64 self-contained package includes .NET/WPF; keep its DLLs, resources, LICENSE, notices and licenses directory together. No SSMS is required. No PostgreSQL server or client tools are bundled. Use PostgreSQL 17/18 and matching-major pg_dump/pg_restore/psql/pg_dumpall for applicable operations. No signed installer, MSI or automatic update is provided.

Connect with the intended PostgreSQL role and appropriate TLS settings (VerifyFull by default). Review SQL, target and role before administering. Only run trusted restore scripts; SQL can issue COMMIT, reconnect or execute local psql commands. Cancel is a request and may arrive after COMMIT. Check the destination before retrying.

Each query tab has its own session. F5 executes selection/document. Ctrl+C copies displayed selected cells with headers; Ctrl+Shift+C omits headers. NULL is distinct from empty text; result/copy/export limits are explicit. Export is not a full database dump.

State defaults to %LOCALAPPDATA%/PGMS; PGMS_STATE_DIRECTORY allows a separate trial directory. Saved credentials, recovery and journals use Windows current-user DPAPI. Close PGMS before backing up state; keep SQL files separately and review recovered tabs with Save As. State/credentials are not universally portable between users/machines or older builds.

Verify integrity with ./Verify-Package.ps1 -PackagePath . in PowerShell 7; manifest hashes detect differences, not authorship. Optional managed installation also requires PowerShell 7. From the extracted package: ./Install-Pgms.ps1 -PackagePath .; inspect ./Get-PgmsInstallation.ps1; launch ./Start-Pgms.ps1; use ./Set-PgmsActiveVersion.ps1 -Milestone <installed-label> for rollback and ./Uninstall-Pgms.ps1 -Milestone <installed-label> for removal. Use -InstallRoot for an isolated managed location. These scripts preserve user state and refuse unmanaged/nonempty roots and unsafe paths; close running instances first.

Source, bilingual guides and reporting: https://github.com/leoars/PostgresManagementStudio . Original work is MIT; third-party licenses and trademark terms remain separate. PGMS is independent and does not claim full SSMS parity or endorsement.

Release assets are built by GitHub Actions from a version tag and published as a draft that the maintainer reviews. The release page provides a single SHA256SUMS.txt covering every asset and a build provenance attestation, verifiable with the GitHub CLI: gh attestation verify <asset> --repo leoars/PostgresManagementStudio. A graphical installer EXE (English/pt-BR, current user or all users) is published alongside this ZIP; the v0.1.0-alpha.1 PowerShell download installer is not part of this release.
