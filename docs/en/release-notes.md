# v0.1.0-alpha.1 — Windows x64

English | [Português (Brasil)](../pt-BR/release-notes.md)

First public binary pre-release of PGMS. This is an alpha for evaluation with synthetic/disposable data. Build label: M1008. Original work is MIT; dependency notices and licenses accompany the package.

## Downloads

From [this release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.1):

| File | Purpose |
| --- | --- |
| `PGMS-0.1.0-alpha.1-win-x64.zip` | Portable application with .NET/WPF runtime. Extract the entire folder and run PGMS.exe. |
| `Install-PGMS-0.1.0-alpha.1.ps1` | Optional PowerShell 7 installer: downloads this exact ZIP, checks its pinned SHA256, then installs for the current user. |
| `SHA256SUMS.txt` | SHA256 values for the ZIP and installer. Hashes check correspondence, not a digital signature. |

The portable application does not need PowerShell 7 or a global .NET runtime. The installer and management scripts require PowerShell 7 and install without changing system execution policy. There is no graphical EXE/MSI installer. Review unsigned scripts according to your organization's policy.

```powershell
Get-FileHash ./PGMS-0.1.0-alpha.1-win-x64.zip -Algorithm SHA256
pwsh -File ./Install-PGMS-0.1.0-alpha.1.ps1
```

The installer defaults to `%LOCALAPPDATA%/Programs/PGMS`, preserves earlier managed versions and refuses an unmanaged nonempty target. `-InstallRoot` chooses another managed location; `-ArchivePath` uses an already downloaded ZIP with the same required hash. It does not launch PGMS or connect to a database. Launch the executable path printed by the installer, or use Start-Pgms.ps1 from the installed version folder. Rollback and removal scripts are included; local profiles/state are retained. See the [distribution guide](distribution.md).

## Graphical installer added after the original release

`PGMS-0.1.0-alpha.1-Setup-x64.exe` and its separate `.sha256` add a native wizard in English/pt-BR, current-user/all-users selection, default desktop shortcut and native uninstall. Application files, original ZIP/assets and tag are unchanged. This installer passed 17 current-user checks and visual review; elevated all-users execution is still untested. See the [graphical installer guide](graphical-installer.md) for requirements, source, build instructions and limits. The PowerShell installer described above remains available.

## Scope

SQL editor and independent sessions, Object Explorer, bounded Results/Messages and CSV/JSON export, reviewed PostgreSQL administration, identity/privileges, session monitoring and logical backup/restore. PostgreSQL 17/18 and same-major client tools are required for database operations and backup/restore; server/tools are not bundled. SSMS is neither required nor distributed.

## Validation

Complete PostgreSQL 17.11 regression passed: **2,115 integration +3,072 WPF =5,187 checks**, using an isolated cluster on loopback port 55441, stopped afterward. Eleven test-port parser checks and two invalid runner-port checks passed separately. A test-only readiness-file race found on the first attempt was corrected with atomic publication; focused process checks passed 26+20, then the full suite above passed. No firewall changes or unrelated process termination were needed.

The final Release package passed manifest, MIT/third-party license and bilingual-guide checks, ZIP extraction validation, and opening/normal exit with its bundled .NET/WPF 10.0.14 runtime. Debug symbols are excluded and a scan found no local developer build paths or user-state files in the package.

The pinned installer passed **12 checks**, including changed-hash rejection, unmanaged-target protection, duplicate-install refusal, installed upgrade/rollback/reactivation and complete removal. Four installed launches used their own runtime and exited 0; external synthetic data was preserved. These checks ran on the development host with isolated state, not a newly certified clean-Windows VM.

Review the [installer source](../../scripts/Install-PgmsRelease.ps1) before running it.

## Known limits

Windows x64 only. Unsigned executable/scripts; SmartScreen has not been tested. No MSI, automatic update or production-support guarantee. Full SSMS parity is not claimed. Results/recovery are bounded; large SQL lines may pause rendering. Cancellation is not proof of rollback. Restore SQL must be trusted and can control transactions, reconnect or execute local psql commands. DPAPI state is tied to the Windows user. Historical physical/clean-Windows observations retain their original scope; a new clean-Windows VM installation of this release is not certified. See [validation](validation.md) and [security](../../SECURITY.md).
