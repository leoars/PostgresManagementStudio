# Começar

[English](../en/getting-started.md) | Português (Brasil)

## Pré-requisitos

O PGMS é destinado a Windows x64. Compilar exige PowerShell 7 e SDK.NET10 compatível com [global.json](../../global.json): 10.0.400 com roll-forward para o patch mais recente. Dependências são fixadas nos lockfiles NuGet. O pacote M1002 foi compilado com SDK 10.0.403 e inclui .NET/WPF 10.0.14; recompilar pode usar outros runtime packs instalados.

PostgreSQL 17/18 com ferramentas da mesma versão principal é o escopo suportado. A matriz histórica testou 17.11/18.6. Não é necessário servidor para abrir o shell. Backup/restore requer pg_dump, pg_restore, psql e pg_dumpall externos conforme a operação; o PGMS não os instala.

```powershell
./scripts/Build.ps1
dotnet run --project src/PGMS.Desktop
```

## Pacote portátil

```powershell
./scripts/Publish.ps1 -SelfContained
./scripts/Verify-Package.ps1 -PackagePath artifacts/M1002-win-x64
./scripts/New-PackageArchive.ps1 -PackagePath artifacts/M1002-win-x64
```

Caminhos seguem `BuildInfo.Milestone`; substitua M1002 se o rótulo mudar. Pastas e ZIPs existentes são recusados, sem sobrescrita. Um clone novo não contém ZIPs ou artefatos locais de aceitação. Extraia todo o pacote e execute PGMS.exe, mantendo bibliotecas e licenças adjacentes. Sem `-SelfContained`, o pacote exige .NET10 Desktop Runtime instalado. SSMS não é necessário.

Instalação gerenciada opcional/update/rollback usa os scripts PowerShell 7 do pacote. Leia o `LEIA-ME.md` daquela versão antes de usá-los. A execução portátil não exige PowerShell 7. O ZIP atual não tem MSI, assinatura ou atualização automática; SmartScreen não foi testado.

## Estado local

Perfis, layout e recuperação ficam normalmente em `%LOCALAPPDATA%/PGMS`. Senhas salvas e rascunhos usam DPAPI do usuário Windows. Não são credenciais portáteis. Para uma avaliação manual isolada, configure uma pasta nova no processo PowerShell atual antes de abrir:

```powershell
$env:PGMS_STATE_DIRECTORY = Join-Path $PWD 'artifacts/manual-trial/state'
dotnet run --project src/PGMS.Desktop
Remove-Item Env:PGMS_STATE_DIRECTORY
```

Feche o PGMS antes de copiar ou alterar seu estado. Guarde cópias dos arquivos SQL importantes separadamente. Veja o [guia de uso](user-guide.md) para conexão e recuperação.
