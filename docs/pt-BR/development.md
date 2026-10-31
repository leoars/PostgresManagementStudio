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

Testes de banco exigem binários de servidor PostgreSQL 17 ou18 e ferramentas correspondentes. O caminho padrão é `C:\Program Files\PostgreSQL\17\bin`; use `-PostgresBin` para outra instalação. O harness cria cluster próprio em loopback sob `artifacts/postgres-test`, usa127.0.0.1:55439 e o encerra ao final. Não deve compartilhar serviço PostgreSQL ativo ou banco do usuário. Diretórios de dados são separados por versão principal. Testes de desktop exigem desktop Windows; algumas provas usam UI Automation nos próprios processos filhos.

```powershell
./scripts/Test-Integration.ps1
./scripts/Test-Integration.ps1 -PostgresBin 'C:\Program Files\PostgreSQL\18\bin'
./scripts/Test-Integration.ps1 -IdentityColumnsOnly
./scripts/Test-Integration.ps1 -BackupFormatsOnly
./scripts/Test-Package.ps1
```

Escolha apenas uma suíte focada por execução. Algumas fixtures criam junctions intencionalmente e podem exigir permissões indisponíveis no sandbox. Testes de clipboard nativo/teclado físico/leitor têm isolamento próprio; não os execute no desktop pessoal nem deduza aprovação física de checks WPF comuns. Leia o script antes de executar prova interativa.

## Empacotar

`Publish.ps1 -SelfContained` compila pacote portátil ReadyToRun com o `BuildInfo.Milestone` atual, copia avisos/licenças e cria manifesto SHA256. `New-PackageArchive.ps1` confere extração antes de gravar ZIP/checksum. Saída existente é recusada. O pacote é identificado pelo manifesto/hash e pelo rótulo de build; recompilações locais não são necessariamente binários idênticos. Publicar fonte não publica ZIP pré-compilado. Veja [começar](getting-started.md).

## Fluxo de alteração

Leia [CONTRIBUTING.pt-BR.md](../../CONTRIBUTING.pt-BR.md) e [AGENTS.md](../../AGENTS.md). Sincronize guias públicos inglês/pt-BR. Informe validação realmente executada e limitações. Registre mudanças relevantes e validação no pull request. Nunca versione perfis, tokens, chaves privadas, dumps, saídas de build ou artefatos locais de aceitação. Gitleaks examina o histórico com `gitleaks git . --log-opts="--all --full-history" --redact=100`. Este repositório de fonte começa com histórico novo e sem exceções históricas da varredura.
