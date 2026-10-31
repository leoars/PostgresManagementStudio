# Validation and limits

English | [Português (Brasil)](../pt-BR/validation.md)

PGMS is in development. M1002 is the internal build label imported into this source repository, not a public release or a guarantee that a local rebuild has the same binary identity. The private operational history and raw acceptance evidence are intentionally not included.

The imported implementation previously passed 5,187 automated checks (2,115 PostgreSQL integration and 3,072 WPF), plus package opening, installed profile preservation and three-draft recovery through rollback/restart. Those are historical observations, not newly executed tests for every source revision. Reproduce current behavior with the public build and test scripts; report your actual environment and results. The clean-import checks for the initial public revision are recorded below after execution.

Known limits: Windows x64; PostgreSQL 17/18 with same-major tools, historical17.11/18.6 coverage;16 and earlier unsupported. No full SSMS parity. Huge SQL lines may pause rendering. Results and recovery have bounded retention. Cancellation is not proof of rollback. DPAPI is user-bound. The package is unsigned, without MSI/automatic update; SmartScreen is not certified. DPI100/125/150/175% were exercised internally;200% was accepted without execution. Automatic reappearance after disconnect, cross-user DPAPI portability and a new clean-Windows test of this import are not certified. External clipboard/keyboard/screen-reader observations are historical and not reproduced by ordinary WPF tests.

```powershell
./scripts/Build.ps1 -Configuration Release
./scripts/Test-Integration.ps1
./scripts/Test-Package.ps1
./scripts/Test-Installation.ps1
./scripts/Test-PublicDocumentation.ps1
```

The database harness creates a disposable loopback cluster under this checkout's ignored artifacts directory, exclusively 127.0.0.1:55439, and stops it afterward. Never use personal or production databases for these tests. See [development](development.md).

## Initial curated-source checks — 2026-10-31

Release build passed without warnings/errors. There were 157 focused desktop file checks, 22 package checks and 46 installation-fixture checks. Public documentation checks covered 12 language pairs and local links. A locally built self-contained package passed manifest/license/bilingual-guide checks, loaded its own runtime, and closed normally with exit0 using isolated state. No binary release was uploaded.

The complete PostgreSQL regression was attempted but stopped before server startup because Windows denied binding the isolated test port 55439 (socket access error). A separate loopback probe reproduced this; dynamic-port binding worked. The full 5,187 historical checks are therefore not claimed as rerun for this import. The disposable cluster was not running afterward. No firewall settings or user databases were changed. Gitleaks found no alerts in the curated source, with no historical exceptions.
