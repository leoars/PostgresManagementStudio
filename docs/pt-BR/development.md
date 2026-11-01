# Desenvolvimento

[English](../en/development.md) | Português (Brasil)

## Compilar e executar

Use Windows x64, PowerShell 7 e o SDK selecionado por `global.json`. Execute na raiz do repositório. `Build.ps1` faz restore com dependências travadas e compila a solução. Nullable e warnings-as-errors estão habilitados. SSMS e REA não são necessários para compilar ou executar PGMS.

```powershell
./scripts/Build.ps1 -Configuration Release
dotnet run --project src/PGMS.Desktop
./scripts/Test-PublicDocumentation.ps1
```

## Testar com isolamento

Testes de banco exigem binários de servidor PostgreSQL 17 ou18 e ferramentas correspondentes. O caminho padrão é `C:\Program Files\PostgreSQL\17\bin`; use `-PostgresBin` para outra instalação. O harness cria cluster próprio em loopback sob `artifacts/postgres-test`, usa 127.0.0.1:55439 por padrão e o encerra ao final. Se a porta estiver ocupada, use `-TestPort` com uma porta livre de loopback; o runner continua criando cluster próprio e recusa anexar a outro servidor. Não deve compartilhar serviço PostgreSQL ativo ou banco do usuário. Diretórios de dados são separados por versão principal. Testes de desktop exigem desktop Windows; algumas provas usam UI Automation nos próprios processos filhos.

```powershell
./scripts/Test-Integration.ps1
./scripts/Test-Integration.ps1 -PostgresBin 'C:\Program Files\PostgreSQL\18\bin'
./scripts/Test-Integration.ps1 -TestPort 55441
./scripts/Test-Integration.ps1 -IdentityColumnsOnly
./scripts/Test-Integration.ps1 -BackupFormatsOnly
./scripts/Test-Package.ps1
```

Escolha apenas uma suíte focada por execução. Algumas fixtures criam junctions intencionalmente e podem exigir permissões indisponíveis no sandbox. Testes de clipboard nativo/teclado físico/leitor têm isolamento próprio; não os execute no desktop pessoal nem deduza aprovação física de checks WPF comuns. Leia o script antes de executar prova interativa.

## Empacotar

`Publish.ps1 -SelfContained` compila pacote portátil ReadyToRun com o `BuildInfo.Milestone` atual, copia avisos/licenças e cria manifesto SHA256. `New-PackageArchive.ps1` confere extração antes de gravar ZIP/checksum. Saída existente é recusada. O pacote é identificado pelo manifesto/hash e pelo rótulo de build; recompilações locais não são necessariamente binários idênticos. Publicar fonte não publica ZIP pré-compilado. Veja [começar](getting-started.md).

Ao publicar uma release, envie um único `SHA256SUMS.txt` cobrindo todos os assets binários (ZIP, instalador PowerShell e EXE do instalador gráfico) em vez de arquivos de checksum por asset, e não substitua assets ou checksums após a publicação; publique uma nova tag.

## Release

Os binários de release saem do [release.yml](../../.github/workflows/release.yml) em um runner Windows hospedado pelo GitHub, não de uma máquina de desenvolvedor. Antes de criar a tag, declare a mesma versão em `Directory.Build.props`, `installer/release.json` (`Version`, `NumericVersion`, `Milestone`) e `src/PGMS.Desktop/BuildInfo.cs`; o workflow falha quando a tag (`v` mais a versão) e esses arquivos divergem. Um push de tag `v*`, ou a execução manual do workflow com o nome da tag, publica o payload ReadyToRun com `Publish.ps1 -SelfContained` em build determinístico, roda `Verify-Package.ps1`, `Test-Package.ps1` e `Test-PackageGuides.ps1`, cria o ZIP com `New-PackageArchive.ps1`, instala o Inno Setup 7.1.1 fixado e conferido por hash em modo portátil e compila o instalador com `Build-GraphicalInstaller.ps1 -SkipFrozenManifest` (o hash de manifesto congelado em `release.json` descreve um payload compilado localmente; no CI o payload vem da própria execução, então só marco e versão do produto são comparados). Em seguida grava `SHA256SUMS.txt`, atesta a proveniência do ZIP e do instalador e cria uma pré-release em **rascunho** com as notas de `docs/en` e `docs/pt-BR`. Publicar o rascunho, e qualquer alteração em release existente, continua decisão manual; uma release já existente para a tag faz o workflow falhar. Uma tag ou execução manual cujo nome traga metadados de build (`v0.1.0-alpha.1+ci.1`) gera um rascunho de teste que deve ser apagado após a revisão, junto com a tag de teste; execuções manuais só são possíveis depois que o workflow existir na `main`. Verifique um asset gerado pelo workflow com:

```powershell
gh attestation verify .\<asset> --repo leoars/PostgresManagementStudio
```

Os jobs `sign-binaries` e `sign-installer` estão reservados ao programa open source da SignPath Foundation e permanecem pulados até a variável de repositório `SIGNPATH_ENABLED` valer `true`; então exigem o secret `SIGNPATH_API_TOKEN` e as variáveis `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG` e `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`. Os binários do aplicativo são assinados antes de entrar no ZIP e no instalador, o instalador é assinado por último, e checksums e atestados cobrem os arquivos finais.

## Fluxo de alteração

Leia [CONTRIBUTING.pt-BR.md](../../CONTRIBUTING.pt-BR.md) e [AGENTS.md](../../AGENTS.md). Sincronize guias públicos inglês/pt-BR. Informe validação realmente executada e limitações. Registre mudanças relevantes e validação no pull request. O GitHub Actions ([build.yml](../../.github/workflows/build.yml)) executa `Build.ps1 -Configuration Release` e `Test-PublicDocumentation.ps1` no Windows em pushes e pull requests; as suítes de banco e WPF não rodam lá e devem ser relatadas manualmente. O Dependabot abre pull requests semanais para pacotes NuGet e actions fixadas. Nunca versione perfis, tokens, chaves privadas, dumps, saídas de build ou artefatos locais de aceitação. Gitleaks examina o histórico com `gitleaks git . --log-opts="--all --full-history" --redact=100`. Este repositório de fonte começa com histórico novo e sem exceções históricas da varredura.
