# Começar

[English](../en/getting-started.md) | Português (Brasil)

[Baixar v0.1.0-alpha.5](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.5): [instalador gráfico](graphical-installer.md), ZIP portátil ou instalador PowerShell 7. O instalador gráfico oferece modos por usuário e para todos, dispensando PowerShell. Veja [notas da versão](release-notes.md).

### Aviso do SmartScreen do Windows

As releases do PGMS ainda não são assinadas digitalmente, então o Windows pode mostrar "O Windows protegeu o computador" na primeira execução do instalador ou do `PGMS.exe`. Clique em **Mais informações** e depois em **Executar assim mesmo**. Antes disso, confirme que o download corresponde aos checksums publicados com a release:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
```

Compare os hashes com o `SHA256SUMS.txt` da release. A partir da v0.1.0-alpha.2 os assets são compilados pelo [workflow de release](../../.github/workflows/release.yml) no GitHub Actions e trazem atestado de proveniência de build, então você também pode confirmar que o ZIP ou o instalador saiu desse workflow e deste repositório (exige o [GitHub CLI](https://cli.github.com/); veja [desenvolvimento](development.md)):

```powershell
gh attestation verify .\PGMS-0.1.0-alpha.5-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

A v0.1.0-alpha.1 foi compilada na máquina do mantenedor e tem apenas checksums. A assinatura de código pelo programa open source da SignPath Foundation está planejada.

## Pré-requisitos

O PGMS é destinado a Windows x64. Compilar exige PowerShell 7 e SDK.NET10 compatível com [global.json](../../global.json): 10.0.400 com roll-forward para o patch mais recente. Dependências são fixadas nos lockfiles NuGet. O pacote M1030 foi compilado com SDK 10.0.403 e inclui .NET/WPF 10.0.14; recompilar pode usar outros runtime packs instalados.

PostgreSQL 16–18 com ferramentas da mesma versão principal é o escopo suportado. A regressão completa roda no 17.11; os recortes do Activity Monitor e do Query Statistics também rodaram no 16.15 e no 18.6. No PostgreSQL 16, o Query Statistics exige `pg_stat_statements` 1.10 ou posterior. Não é necessário servidor para abrir o shell. Backup/restore requer pg_dump, pg_restore, psql e pg_dumpall externos conforme a operação; o PGMS não os instala.

```powershell
./scripts/Build.ps1
dotnet run --project src/PGMS.Desktop
```

## Pacote portátil

```powershell
./scripts/Publish.ps1 -SelfContained
./scripts/Verify-Package.ps1 -PackagePath artifacts/M1030-win-x64
./scripts/New-PackageArchive.ps1 -PackagePath artifacts/M1030-win-x64
```

Caminhos seguem `BuildInfo.Milestone`; substitua M1030 se o rótulo mudar. Pastas e ZIPs existentes são recusados, sem sobrescrita. Um clone novo não contém ZIPs ou artefatos locais de aceitação. Extraia todo o pacote e execute PGMS.exe, mantendo bibliotecas e licenças adjacentes. Sem `-SelfContained`, o pacote exige .NET10 Desktop Runtime instalado. SSMS não é necessário.

Instalação gerenciada opcional/update/rollback usa os scripts PowerShell 7 do pacote. Leia o `LEIA-ME.md` daquela versão antes de usá-los. A execução portátil não exige PowerShell 7. O ZIP atual não tem MSI, assinatura ou atualização automática; SmartScreen não foi testado.

## Estado local

Perfis, layout e recuperação ficam normalmente em `%LOCALAPPDATA%/PGMS`. Senhas salvas e rascunhos usam DPAPI do usuário Windows. Não são credenciais portáteis. Para uma avaliação manual isolada, configure uma pasta nova no processo PowerShell atual antes de abrir:

```powershell
$env:PGMS_STATE_DIRECTORY = Join-Path $PWD 'artifacts/manual-trial/state'
dotnet run --project src/PGMS.Desktop
Remove-Item Env:PGMS_STATE_DIRECTORY
```

Feche o PGMS antes de copiar ou alterar seu estado. Guarde cópias dos arquivos SQL importantes separadamente. Veja o [guia de uso](user-guide.md) para conexão e recuperação.
