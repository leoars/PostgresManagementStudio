# Graphical installer

English | [Português (Brasil)](../pt-BR/graphical-installer.md)

Download `PGMS-0.1.0-alpha.3-Setup-x64.exe` from the [alpha.3 release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.3) and check it against `SHA256SUMS.txt` (and, optionally, `gh attestation verify`). Since v0.1.0-alpha.3 the installer is built by the release workflow from the same payload as the portable ZIP of that release; in v0.1.0-alpha.1 it was added later to wrap the existing M1022 payload. The installer does not require PowerShell or a separately installed .NET runtime.

## Installation choices

Choose English or Brazilian Portuguese, then the installation mode:

- **Only for me:** no administrator privileges; default `%LOCALAPPDATA%\Programs\PostgresManagementStudio`.
- **For all users:** Windows requests administrator privileges; default `%ProgramFiles%\PostgresManagementStudio`. Program files and shortcuts are available to all users.

The desktop shortcut is selected by default and can be disabled. A Start menu shortcut and an Installed Apps/uninstaller entry are created. Opening PGMS at the end is optional and unchecked. This installer targets Windows x64, Windows 10 build 19045 or later; other architectures are not certified.

Connections, saved credentials, layout and recovery remain under each user's `%LOCALAPPDATA%\PGMS`, with user-scoped DPAPI. Global installation does not share credentials or install PostgreSQL servers/tools. Close PGMS before installation or repair. No automatic updater or migration from the PowerShell installation is included. The PowerShell installation uses a different root (`Programs\PGMS`); remove an old installation using its own removal method after checking which shortcut you use.

## Repair and removal

Run Setup again with the same mode and directory to repair this version. Unknown nonempty directories and linked directories are refused. A locked PGMS executable blocks repair instead of forcibly closing the app. Native uninstall removes its own installed files and shortcuts; profiles, recovery and additional user SQL files are preserved. Keep important SQL files outside the installation directory and maintain separate backups.

The executable is unsigned; SmartScreen reputation and a clean Windows VM have not been certified. Current-user installation, exact payload hashes, bundled-runtime launch, Portuguese repair, native removal, default desktop shortcut, rejected unmanaged/linked/locked targets and preservation of synthetic user files passed 17 checks. Portuguese wizard pages through Ready and English welcome/cancellation were visually reviewed. **All-users elevation and execution have not yet been tested**; neither has a later-version upgrade.

## Build and test

Use the official [Inno Setup 7.1.1 x64 compiler](https://jrsoftware.org/isdl.php), retaining its license. Extract the existing release ZIP into `artifacts/M1022-win-x64`. The builder checks every payload file, the frozen manifest and application version, and refuses an existing output directory. A local application rebuild may have a different manifest and is intentionally refused by this release-specific builder. Update the release configuration for a future frozen release. The [release workflow](../../.github/workflows/release.yml) passes `-SkipFrozenManifest` because it builds the payload itself; milestone and product version are still enforced, and the frozen hash remains required for local builds.

```powershell
./scripts/Build-GraphicalInstaller.ps1 -PackagePath artifacts/M1022-win-x64 -CompilerPath 'C:\Tools\Inno Setup 7\ISCC.exe'
./scripts/Test-GraphicalInstaller.ps1 -InstallerPath artifacts/graphical-installer/0.1.0-alpha.1/PGMS-0.1.0-alpha.1-Setup-x64.exe
```

The test performs a real current-user installation and native uninstall, using a unique directory and Start menu group. It refuses to run if this product is already registered or its desktop shortcut exists. Run in an isolated Windows account; the test preserves its evidence under ignored `artifacts/graphical-tests`. It does not elevate or use a database. Compiler tooling is not an application runtime dependency. The installer retains the Inno Setup credits and installs `INNO-SETUP-LICENSE.txt` alongside its native uninstaller.
