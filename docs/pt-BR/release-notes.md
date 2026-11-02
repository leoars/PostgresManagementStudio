# v0.1.0-alpha.3 — Windows x64

[English](../en/release-notes.md) | Português (Brasil)

Terceira pré-release pública do PGMS. É uma versão alpha para avaliação com dados sintéticos/descartáveis. Rótulo de build: M1022. Ao contrário da v0.1.0-alpha.2, esta release muda o aplicativo: acrescenta Query Statistics sobre o `pg_stat_statements`, impede que placeholders `$n` fechem a sessão da consulta e dá às linhas das grades nomes curtos para leitores de tela. Os binários são compilados e atestados pelo mesmo [workflow de release](../../.github/workflows/release.yml) da alpha.2. Trabalho próprio MIT; avisos e licenças das dependências acompanham o pacote.

## O que mudou desde a v0.1.0-alpha.2

- **Query Statistics (View → Query Statistics…).** Análise somente leitura do `pg_stat_statements` do banco conectado, com propósito parecido com o Query Store do SQL Server: chamadas, tempo de execução (total, médio, mínimo, máximo, desvio padrão), linhas, % de acerto em cache, blocos compartilhados/temporários, tempo de I/O e bytes de WAL por statement normalizado. Filtros e ordenação no servidor com limite de 1.000 linhas, cabeçalhos ordenáveis, gráfico top-N ao lado da grade, janelas acumulada/desde a linha de base/últimos 5, 15 ou 60 minutos, **Show Plan** com `EXPLAIN (GENERIC_PLAN)` numa nova aba de consulta, reset por statement e total com confirmação, auto-refresh opcional e filtros gravados por alvo de conexão. O PGMS nunca instala nem configura a extensão; a janela explica o que falta no servidor. Veja o [guia de uso](user-guide.md#estatísticas-de-consultas).
- **Placeholders `$n` não fecham mais a sessão.** Executar, explicar ou analisar SQL com placeholders do tipo `$1` falhava com o erro de protocolo 08P01, e o driver então fechava a conexão da aba. Agora o PGMS detecta os placeholders antes de enviar (ignorando literais, identificadores entre aspas, comentários e dollar quotes; `PREPARE` e corpos de funções/procedures ficam de fora), recusa o statement com **Go to Error** no placeholder e mantém a sessão aberta. **Estimated Plan** passa a usar um plano genérico.
- **Nomes mais curtos para linhas das grades em leitores de tela.** Todas as grades WPF, inclusive os resultados de consultas, nomeiam cada linha pelas primeiras colunas visíveis (por exemplo `Query ID 42, Database app, …`) em vez de expor o registro inteiro.

## Downloads

Nesta [release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.3):

| Arquivo | Finalidade |
| --- | --- |
| `PGMS-0.1.0-alpha.3-Setup-x64.exe` | Instalador gráfico (inglês/pt-BR). Por usuário sem direitos de administrador, ou para todos com elevação. Não exige PowerShell nem runtime .NET separado. |
| `PGMS-0.1.0-alpha.3-win-x64.zip` | Aplicativo portátil com runtime .NET/WPF. Extraia toda a pasta e execute PGMS.exe. Inclui os scripts gerenciados PowerShell 7. |
| `SHA256SUMS.txt` | SHA256 de todos os assets desta release. Hash confere correspondência, não autoria. |
| `package-manifest.json` | Manifesto SHA256 de cada arquivo dentro do ZIP, conferido por `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | Estas notas. |

## Verifique o que baixou

Os binários não são assinados digitalmente, então o SmartScreen do Windows pode avisar na primeira execução. Antes de escolher **Executar assim mesmo**, confirme o checksum e a proveniência:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.3-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare os hashes com o `SHA256SUMS.txt`. O segundo comando exige o [GitHub CLI](https://cli.github.com/) e confirma que o arquivo foi produzido pelo workflow de release deste repositório no commit da tag; execute-o também para o ZIP. A assinatura de código pelo programa open source da SignPath Foundation está planejada; nenhuma data é prometida.

## Escopo

Editor SQL e sessões independentes, Object Explorer, Results/Messages com limites e exportação CSV/JSON, administração PostgreSQL revisada, identity/privilégios, monitor de sessões, estatísticas de consultas e backup/restore lógico. PostgreSQL 17/18 e ferramentas da mesma versão principal são necessários para operações com banco e backup/restore; não são incluídos. Query Statistics também exige a extensão `pg_stat_statements` no servidor. SSMS não é exigido nem distribuído.

## Validação

Comportamento do aplicativo: a fonte M1022 passou na regressão completa PostgreSQL 17.11 deste repositório (**2.236 integração + 3.315 WPF = 5.551 checks**) em cluster isolado em loopback iniciado com `pg_stat_statements` pré-carregado; o serviço PostgreSQL do host não é usado. A suíte focada de Query Statistics (`Test-Integration.ps1 -QueryStatisticsOnly`) passou em 90 checks de integração + 224 WPF. A regressão não roda no CI. A janela Query Statistics também foi comparada com o Query Store do SSMS 22.11 sob carga sintética idêntica; as diferenças estão em [validação](validation.md).

Pipeline de release: igual ao da v0.1.0-alpha.2. O workflow valida que a tag corresponde a `installer/release.json`, `Directory.Build.props` e `BuildInfo.cs`; publica com `Publish.ps1 -SelfContained`; roda `Verify-Package.ps1`, `Test-Package.ps1` e `Test-PackageGuides.ps1`; cria o ZIP; compila o instalador com o Inno Setup 7.1.1 fixado e conferido por hash; executa um smoke informacional de abertura no runner; atesta o ZIP e o instalador; e abre uma pré-release em rascunho que o mantenedor revisa antes de publicar. Os assets do rascunho são verificados localmente antes da publicação, como na alpha.2, e o resultado é relatado no pull request da release.

Instalação para todos os usuários (elevada), instalação em VM Windows limpa e observações físicas de DPI/teclado/leitor de tela da nova janela não recebem nova certificação nesta release.

## Limites conhecidos

Somente Windows x64. Executável e instalador sem assinatura; o SmartScreen avisa. Sem MSI, atualização automática ou garantia de suporte em produção. Não se declara paridade integral SSMS. Query Statistics é um snapshot de contadores acumulados do servidor, não monitoramento contínuo: janelas deslizantes só amostram com a janela aberta, mínimo/máximo não existem para uma janela e o PostgreSQL não tem tempo de CPU por statement. A detecção de placeholders supõe `standard_conforming_strings = on` e não oferece diálogo de valores de parâmetros. Resultados/recuperação têm limites; linhas SQL grandes podem pausar renderização. Cancelar não comprova rollback. SQL de restore exige confiança e pode controlar transações, reconectar ou executar comandos locais psql. Estado DPAPI depende do usuário Windows. O hash de manifesto congelado em `installer/release.json` descreve um payload compilado localmente, não o do CI; o build no CI compara marco e versão do produto. Veja [validação](validation.md) e [segurança](../../SECURITY.pt-BR.md).

## Releases anteriores

A v0.1.0-alpha.2 (01/11/2026, primeira compilada pelo CI com atestado) e a v0.1.0-alpha.1 (31/10/2026) continuam disponíveis com seus assets e notas originais.
