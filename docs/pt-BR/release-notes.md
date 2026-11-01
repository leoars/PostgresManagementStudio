# v0.1.0-alpha.2 — Windows x64

[English](../en/release-notes.md) | Português (Brasil)

Segunda pré-release pública do PGMS e a primeira compilada por integração contínua. É uma versão alpha para avaliação com dados sintéticos/descartáveis. Rótulo de build: M1008, a mesma fonte do aplicativo da v0.1.0-alpha.1; esta release muda como os binários são produzidos, verificados e instalados, não o código do aplicativo. Trabalho próprio MIT; avisos e licenças das dependências acompanham o pacote.

## O que mudou desde a v0.1.0-alpha.1

- **Compilada pelo GitHub Actions.** O ZIP e o instalador gráfico saem do [workflow de release](../../.github/workflows/release.yml) em um runner Windows hospedado pelo GitHub, a partir do commit da tag, com build determinístico (`ContinuousIntegrationBuild`, `Deterministic`) e restore NuGet travado. A v0.1.0-alpha.1 foi compilada na máquina do mantenedor.
- **Atestado de proveniência de build.** O ZIP e o instalador trazem um atestado de artefato do GitHub que vincula cada arquivo a este repositório, ao workflow e ao commit. Verifique com o GitHub CLI (veja abaixo).
- **Instalador gráfico na mesma release e do mesmo payload.** `PGMS-0.1.0-alpha.2-Setup-x64.exe` (Inno Setup 7.1.1, inglês/pt-BR, por usuário ou para todos, desinstalação nativa) é compilado na mesma execução a partir do payload exato empacotado no ZIP. Na v0.1.0-alpha.1 o instalador foi adicionado à release depois.
- **Um único arquivo de checksums.** `SHA256SUMS.txt` cobre todos os assets; não há arquivos `.sha256` por asset.
- **Sem instalador PowerShell de download.** `Install-PGMS-<versão>.ps1` não é publicado nesta release; os scripts gerenciados PowerShell 7 dentro do ZIP (instalar, abrir, rollback, remover) continuam disponíveis.
- **Documentação.** Novo topo do README (proposta de valor, Instalar / Compilar a partir do código), orientação sobre o SmartScreen do Windows com verificação de checksum e atestado, processo de release documentado em [desenvolvimento](development.md).

O aplicativo em si não mudou: a árvore `src/` é idêntica à da v0.1.0-alpha.1 (marco M1008). As mudanças na fonte pública desde a alpha.1 se limitam às fontes do instalador gráfico, scripts de build/teste, workflows de CI, arquivos de comunidade e documentação.

## Downloads

Nesta [release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.2):

| Arquivo | Finalidade |
| --- | --- |
| `PGMS-0.1.0-alpha.2-Setup-x64.exe` | Instalador gráfico (inglês/pt-BR). Por usuário sem direitos de administrador, ou para todos com elevação. Não exige PowerShell nem runtime .NET separado. |
| `PGMS-0.1.0-alpha.2-win-x64.zip` | Aplicativo portátil com runtime .NET/WPF. Extraia toda a pasta e execute PGMS.exe. Inclui os scripts gerenciados PowerShell 7. |
| `SHA256SUMS.txt` | SHA256 de todos os assets desta release. Hash confere correspondência, não autoria. |
| `package-manifest.json` | Manifesto SHA256 de cada arquivo dentro do ZIP, conferido por `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | Estas notas. |

## Verifique o que baixou

Os binários não são assinados digitalmente, então o SmartScreen do Windows pode avisar na primeira execução. Antes de escolher **Executar assim mesmo**, confirme o checksum e a proveniência:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.2-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare os hashes com o `SHA256SUMS.txt`. O segundo comando exige o [GitHub CLI](https://cli.github.com/) e confirma que o arquivo foi produzido pelo workflow de release deste repositório no commit da tag; execute-o também para o ZIP. A assinatura de código pelo programa open source da SignPath Foundation está planejada; nenhuma data é prometida.

## Escopo

Editor SQL e sessões independentes, Object Explorer, Results/Messages com limites e exportação CSV/JSON, administração PostgreSQL revisada, identity/privilégios, monitor e backup/restore lógico. PostgreSQL 17/18 e ferramentas da mesma versão principal são necessários para operações com banco e backup/restore; não são incluídos. SSMS não é exigido nem distribuído.

## Validação

Comportamento do aplicativo: a fonte M1008 já passou na regressão completa PostgreSQL 17.11 (**2.115 integração + 3.072 WPF = 5.187 checks**) em cluster isolado em loopback, conforme registrado nas notas da v0.1.0-alpha.1 e em [validação](validation.md). Essa suíte não foi repetida para esta release porque a fonte do aplicativo não mudou; ela não roda no CI.

Pipeline de release: o workflow valida que a tag corresponde a `installer/release.json`, `Directory.Build.props` e `BuildInfo.cs`; publica com `Publish.ps1 -SelfContained` (SDK escolhido por `global.json`, .NET/WPF 10.0.14 incluído, ReadyToRun); roda `Verify-Package.ps1`, `Test-Package.ps1` e `Test-PackageGuides.ps1`; cria o ZIP com `New-PackageArchive.ps1`; instala o Inno Setup 7.1.1 fixado e conferido por hash e compila o instalador; executa um smoke informacional de abertura no runner; atesta o ZIP e o instalador; e abre uma pré-release em rascunho que o mantenedor revisa antes de publicar. Um ensaio do mesmo workflow no commit anterior (tag de teste `v0.1.0-alpha.1+ci.1`, depois apagada) concluiu, e seus assets passaram na comparação de checksums, `gh attestation verify`, `Verify-Package.ps1`, `Test-PackageArchive.ps1`, `Test-PackageGuides.ps1`, `Test-PackageLaunch.ps1` e nos 17 checks por usuário do `Test-GraphicalInstaller.ps1` na máquina do mantenedor. A mesma verificação local é repetida nos assets do rascunho desta release antes da publicação e relatada no pull request da release.

Instalação para todos os usuários (elevada), instalação em VM Windows limpa e observações físicas de DPI/teclado/leitor de tela não recebem nova certificação nesta release.

## Limites conhecidos

Somente Windows x64. Executável e instalador sem assinatura; o SmartScreen avisa. Sem MSI, atualização automática ou garantia de suporte em produção. Não se declara paridade integral SSMS. Resultados/recuperação têm limites; linhas SQL grandes podem pausar renderização. Cancelar não comprova rollback. SQL de restore exige confiança e pode controlar transações, reconectar ou executar comandos locais psql. Estado DPAPI depende do usuário Windows. O hash de manifesto congelado em `installer/release.json` descreve um payload compilado localmente, não o do CI; o build no CI compara marco e versão do produto. Veja [validação](validation.md) e [segurança](../../SECURITY.pt-BR.md).

## Release anterior

A v0.1.0-alpha.1 (31/10/2026) continua disponível com seus assets e notas originais; foi compilada na máquina do mantenedor e tem checksums, mas não atestado.
