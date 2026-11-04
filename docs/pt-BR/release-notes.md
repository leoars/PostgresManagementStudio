# v0.1.0-alpha.4 — Windows x64

[English](../en/release-notes.md) | Português (Brasil)

Quarta pré-release pública do PGMS. É uma versão alpha para avaliação com dados sintéticos/descartáveis. Rótulo de build: M1030. Esta release refaz o Activity Monitor como aba de documento com os painéis do SSMS, acrescenta detach concorrente e subpartições ao diálogo Partitions e passa a suportar o PostgreSQL 16. Os binários são compilados e atestados pelo mesmo [workflow de release](../../.github/workflows/release.yml) da alpha.2 e da alpha.3. Trabalho próprio MIT; avisos e licenças das dependências acompanham o pacote.

## O que mudou desde a v0.1.0-alpha.3

- **Activity Monitor em aba de documento, com seis painéis.** **View → Activity Monitor** (`Ctrl+Alt+A`) abre uma aba por servidor e role, reaproveitada ao abrir de novo. Painéis recolhíveis como no SSMS: **Overview** (gráficos de sessões ativas, sessões em espera, I/O do banco via `pg_stat_io` e transações por segundo), **Processes**, **Resource Waits** (categorias de espera amostradas), **Data File I/O** (por database), **Recent Expensive Queries** (variação do `pg_stat_statements` entre atualizações) e **Active Expensive Queries** (com **Edit Query Text** e **Show Execution Plan**, plano estimado que não executa a consulta). O menu de contexto do Overview define o intervalo e pausa, retoma ou atualiza o monitor; em pausa, o título mostra "Overview (paused)". Veja o [guia de uso](user-guide.md#activity-monitor).
- **Lista de sessões mais próxima do SSMS.** Colunas Host, **Blocked by PID** e **Head blocker** (calculado sobre `pg_locks` do servidor inteiro), dicas nos cabeçalhos, menu da linha (**Details…**, **Cancel Query**, **Terminate Session…**), diálogo **Session Details** com a consulta mais recente, filtros no servidor e páginas por PID (First/Previous/Next). A atualização automática começa ligada a cada 10 segundos, com intervalos de 1 segundo a 1 hora.
- **Partições.** **Detach concurrently** executa `DETACH PARTITION … CONCURRENTLY` fora de transação, sem bloquear consultas, com checagem prévia de partição DEFAULT e de outro detach pendente; uma partição pendente é concluída com `FINALIZE`. O diálogo também cria uma partição já particionada (**Sub-partition by**, com chave de colunas do pai), anexa tabelas particionadas e desanexa partições particionadas preservando a subárvore.
- **PostgreSQL 16.** O escopo suportado passa a ser PostgreSQL 16 a 18. O Query Statistics e o Recent Expensive Queries leem o `pg_stat_statements` 1.10 do PostgreSQL 16; versões anteriores à 1.10 são recusadas com a indicação do `ALTER EXTENSION … UPDATE` necessário.
- **Ajustes visuais.** Abas de documento inativas usam texto escuro e a seleção inativa nas grades fica azul, como no SSMS.

## Downloads

Nesta [release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.4):

| Arquivo | Finalidade |
| --- | --- |
| `PGMS-0.1.0-alpha.4-Setup-x64.exe` | Instalador gráfico (inglês/pt-BR). Por usuário sem direitos de administrador, ou para todos com elevação. Não exige PowerShell nem runtime .NET separado. |
| `PGMS-0.1.0-alpha.4-win-x64.zip` | Aplicativo portátil com runtime .NET/WPF. Extraia toda a pasta e execute PGMS.exe. Inclui os scripts gerenciados PowerShell 7. |
| `SHA256SUMS.txt` | SHA256 de todos os assets desta release. Hash confere correspondência, não autoria. |
| `package-manifest.json` | Manifesto SHA256 de cada arquivo dentro do ZIP, conferido por `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | Estas notas. |

## Verifique o que baixou

Os binários não são assinados digitalmente, então o SmartScreen do Windows pode avisar na primeira execução. Antes de escolher **Executar assim mesmo**, confirme o checksum e a proveniência:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.4-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare os hashes com o `SHA256SUMS.txt`. O segundo comando exige o [GitHub CLI](https://cli.github.com/) e confirma que o arquivo foi produzido pelo workflow de release deste repositório no commit da tag; execute-o também para o ZIP. A assinatura de código pelo programa open source da SignPath Foundation está planejada; nenhuma data é prometida.

## Escopo

Editor SQL e sessões independentes, Object Explorer, Results/Messages com limites e exportação CSV/JSON, administração PostgreSQL revisada (inclusive partições), identity/privilégios, Activity Monitor, estatísticas de consultas e backup/restore lógico. PostgreSQL 16 a 18 e ferramentas da mesma versão principal são necessários para operações com banco e backup/restore; não são incluídos. Query Statistics e Recent Expensive Queries também exigem a extensão `pg_stat_statements` no servidor. SSMS não é exigido nem distribuído.

## Validação

Comportamento do aplicativo: a fonte M1030 passou na regressão completa PostgreSQL 17.11 deste repositório (**2.318 integração + 3.429 WPF = 5.747 checks**) em cluster isolado em loopback iniciado com `pg_stat_statements` pré-carregado; o serviço PostgreSQL do host não é usado. Na mesma fonte, a suíte focada do Activity Monitor (`Test-Integration.ps1 -ActivityOnly`) passou em 71 checks de integração + 152 WPF no PostgreSQL 16.15 e no 18.6; a do Query Statistics passou no 16.15 e no 18.6 sobre a fonte M1029, e o código do Query Statistics não mudou depois dela. A regressão não roda no CI. O Activity Monitor foi comparado duas vezes com o do SSMS 22.11 sob a mesma carga de bloqueio; as diferenças restantes estão em [validação](validation.md).

Pipeline de release: igual ao da v0.1.0-alpha.2. O workflow valida que a tag corresponde a `installer/release.json`, `Directory.Build.props` e `BuildInfo.cs`; publica com `Publish.ps1 -SelfContained`; roda `Verify-Package.ps1`, `Test-Package.ps1` e `Test-PackageGuides.ps1`; cria o ZIP; compila o instalador com o Inno Setup 7.1.1 fixado e conferido por hash; executa um smoke informacional de abertura no runner; atesta o ZIP e o instalador; e abre uma pré-release em rascunho que o mantenedor revisa antes de publicar. Os assets do rascunho são verificados localmente antes da publicação, e o resultado é relatado no pull request da release.

Instalação para todos os usuários (elevada), instalação em VM Windows limpa, regressão completa no PostgreSQL 16 e 18 e observações físicas de DPI/teclado/leitor de tela das novas telas não recebem nova certificação nesta release.

## Limites conhecidos

Somente Windows x64. Executável e instalador sem assinatura; o SmartScreen avisa. Sem MSI, atualização automática ou garantia de suporte em produção. Não se declara paridade integral SSMS. Activity Monitor: Data File I/O é por database, não por arquivo (o PostgreSQL não tem estatísticas por arquivo); Resource Waits é amostrado a cada atualização, não acumulado; as páginas de sessões são leituras separadas e a ordenação vale dentro da página; nenhum teste com mais de 1.000 sessões. Query Statistics é um snapshot de contadores acumulados do servidor, não monitoramento contínuo; no `pg_stat_statements` 1.10 uma janela de tempo pode contar a menos um statement removido e rastreado de novo. A detecção de placeholders supõe `standard_conforming_strings = on` e não oferece diálogo de valores de parâmetros. Resultados/recuperação têm limites; linhas SQL grandes podem pausar renderização. Cancelar não comprova rollback. SQL de restore exige confiança e pode controlar transações, reconectar ou executar comandos locais psql. Estado DPAPI depende do usuário Windows. O hash de manifesto congelado em `installer/release.json` descreve um payload compilado localmente, não o do CI; o build no CI compara marco e versão do produto. Veja [validação](validation.md) e [segurança](../../SECURITY.pt-BR.md).

## Releases anteriores

A v0.1.0-alpha.3 (02/11/2026, Query Statistics), a v0.1.0-alpha.2 (01/11/2026, primeira compilada pelo CI com atestado) e a v0.1.0-alpha.1 (31/10/2026) continuam disponíveis com seus assets e notas originais.
