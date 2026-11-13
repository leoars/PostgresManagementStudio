# v0.1.0-alpha.6 — Windows x64

[English](../en/release-notes.md) | Português (Brasil)

Sexta pré-release pública do PGMS. É uma alpha para avaliação com dados sintéticos ou descartáveis. Rótulo de build: M1062. Os binários são gerados e atestados pelo mesmo [workflow de release](../../.github/workflows/release.yml) das versões anteriores. O trabalho próprio está sob Apache-2.0, com créditos no `NOTICE`; as versões até a alpha.4 continuam sob MIT.

## O que mudou desde a v0.1.0-alpha.5

- **Índices nos diagramas de banco.** Cada tabela de um diagrama tem uma seção **Indexes**, recolhida por padrão, com uma linha por índice quando aberta: método (GIN, GiST, SP-GiST, BRIN, Hash), colunas ou expressões da chave, classe de operadores quando não é a padrão (como `gin_trgm_ops`), `INCLUDE`, UNIQUE, EXCLUDE, parcial e inválido, com o predicado e o `CREATE INDEX` completo na dica. Um aviso marca a tabela com chave estrangeira que nenhum índice atende e abre **Find Unindexed Foreign Keys…**, que também chega nesta mudança: num banco do Object Explorer, lista essas chaves pelo catálogo e cria os índices a partir de um script revisado, de forma concorrente ou em uma transação. **Show Indexes** e **Refresh** ficam no menu do diagrama. Cada gravação escreve os índices de cada tabela na definição do diagrama para ferramentas e agentes de IA que a leem; diagramas antigos abrem sem mudança. Só leitura: índices continuam sendo criados em **Indexes/Keys…**. O SSMS 22.11 não mostra índices no diagrama. Sugestão de um usuário do PGMS.
- **Descrições nos diagramas de banco e exportação para IA.** Tabela, coluna e relação de um diagrama podem ter descrição, que é o `COMMENT ON` do objeto no PostgreSQL, então qualquer ferramenta que lê o catálogo a enxerga. Edite em **Description Property** pelo menu da tabela, da coluna ou da relação (ou clique no pequeno ícone de página que marca o item descrito, com o texto na dica); **Save** aplica depois da revisão do script, como toda mudança do diagrama. Se você não é o dono da tabela, a descrição fica no diagrama como não aplicada e o PGMS mostra o script para enviar ao dono. Cada gravação guarda uma cópia das descrições na definição do diagrama (formato 3); quando o banco mudou alguma depois disso, o diagrama mostra o texto do banco e um ícone laranja. **Export for AI…** escreve as tabelas do diagrama em Markdown (colunas, chaves, relações, índices e descrições, em ordem fixa, com uma instrução para o agente no topo) para copiar ou salvar, para um system prompt, um `CLAUDE.md` ou um `AGENTS.md`. A função opcional `pgms.diagram_markdown(name)`, criada só depois de você revisar o script, devolve o mesmo texto a um agente conectado ao banco. Veja [Usando diagramas com agentes de IA](user-guide.md#usando-diagramas-com-agentes-de-ia). No SSMS 22.11 as descrições são editadas na janela Properties; os ícones, a cópia e a exportação são acréscimos do PGMS. Sugestão de um usuário do PGMS.
- **Janelas abertas a partir de um objeto seguem esse objeto.** **Restore Database…** e **Back Up…** pedem o banco quando o servidor ou a pasta Databases estão selecionados, em vez de usar o banco de login, e um nome novo cria o banco para a restauração; **Pending Index Operation…** de uma tabela mostra só a operação dela; **Object Permissions…**, **Default Permissions…** e **Tools ▸ Table Operations…** partem da seleção; os diálogos de operação de uma tabela funcionam também a partir de pasta, índice ou coluna dela; a aba de plano não oferece mais **Show Execution Plan In New Tab**, que nela não fazia nada. No diagrama, o **Table View** sem tabela selecionada muda todas as tabelas, e o **Undo** passa a desfazer também a troca de Table View.
- **Histórico de consultas.** **View ▸ Query History…** (`Ctrl+Alt+H`) lista o SQL executado nas abas de consulta, com servidor, banco, usuário, início, duração, linhas, resultado e origem (digitado, Query Designer, IA, outro). Consultas repetidas viram uma linha com a última execução e a contagem, que se expande em cada execução. Filtre por texto, conexão e período; reabra numa aba nova sem executar, copie, fixe ou apague. Fica só neste computador, criptografado com DPAPI do Windows para o usuário Windows atual, e nunca é enviado ao servidor. A retenção fica em **Tools ▸ Options ▸ Query Execution ▸ Query History** (padrões: 90 dias, 10.000 execuções, 64 KiB de SQL por execução), e um perfil de conexão salvo pode desligar a gravação para o servidor dele. O SSMS 22.11 não tem equivalente nativo. Sugestão de um usuário do PGMS.
- **Script as no Object Explorer.** **Script … as** (CREATE, DROP e os verbos que se aplicam, para uma janela de consulta, um arquivo ou a área de transferência) em bancos, schemas, tabelas, views, sequences, funções, procedures, enums, domínios, índices, chaves, constraints, triggers e roles; pastas **Keys**, **Constraints** e **Triggers** sob a tabela; **Rebuild** de índice e **Rebuild All**; **Take Offline** e **Bring Online** de banco (`ALLOW_CONNECTIONS`), com o banco offline marcado na árvore.
- **Avisos nos planos de execução gráficos.** No plano real, um operador cujo filtro descartou muitas linhas mostra um triângulo amarelo e uma seção **Warnings** com as contagens de Filter, Join Filter e Index Recheck. Os limiares (10 vezes as linhas retornadas e pelo menos 1.000 linhas por padrão) ficam em Query Options e Tools ▸ Options. É uma regra do PGMS; o PostgreSQL não emite avisos no plano.
- **Sugestão de índice a partir do plano.** Para uma varredura com aviso e filtros simples, uma linha verde no cabeçalho do plano e **Missing Index Details…** abrem um script `CREATE INDEX` comentado, conferido no catálogo. Nada é executado. É uma heurística sem estimativa de seletividade; não cobre junções, `OR`, expressões nem `INCLUDE`.
- **Gerar SQL com IA em bancos grandes.** Quando a estrutura não cabe no limite do contexto (agora configurável, 200.000 caracteres por padrão), o provedor primeiro escolhe as tabelas relevantes pelos nomes, e as tabelas enviadas seguem essa escolha, as chaves estrangeiras e as palavras em comum com o pedido. **View Sent Context…** mostra as duas chamadas.

## Downloads

Em [esta release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.6):

| Arquivo | Finalidade |
| --- | --- |
| `PGMS-0.1.0-alpha.6-Setup-x64.exe` | Instalador gráfico (inglês/pt-BR). Usuário atual sem administrador, ou todos os usuários com elevação. Não exige PowerShell nem runtime .NET separado. |
| `PGMS-0.1.0-alpha.6-win-x64.zip` | Aplicativo portátil com runtime .NET/WPF. Extraia a pasta inteira e execute PGMS.exe. Inclui os scripts PowerShell 7 gerenciados. |
| `SHA256SUMS.txt` | SHA256 de cada arquivo desta release. Hashes conferem correspondência, não autoria. |
| `package-manifest.json` | Manifesto SHA256 de cada arquivo dentro do ZIP, conferido por `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | Estas notas. |

## Confira o que você baixou

Os binários não têm assinatura de código, então o Windows SmartScreen pode avisar na primeira execução. Antes de escolher **Executar assim mesmo**, confira o checksum e a origem:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.6-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare os hashes com `SHA256SUMS.txt`. O segundo comando exige o [GitHub CLI](https://cli.github.com/) e confirma que o arquivo foi gerado pelo workflow de release deste repositório no commit da tag; rode também para o ZIP. A assinatura de código pelo programa open source da SignPath Foundation está planejada, sem data prometida.

## Escopo

Editor SQL com IntelliSense e sessões independentes, Object Explorer, Results em páginas e Results to Text/File, exportação CSV/JSON, diagramas de banco e designers, planos gráficos, administração PostgreSQL revisada, identidade/privilégios, Activity Monitor, estatísticas de consultas e backup/restore lógico. PostgreSQL 16 a 18 e ferramentas cliente da mesma versão principal são necessários para operações no banco e backup/restore; servidor e ferramentas não acompanham o pacote. O SSMS não é exigido nem distribuído.

## Validação

Comportamento do aplicativo: a fonte M1062 passou na regressão completa deste repositório no PostgreSQL 17.11 (**3.018 de integração + 4.586 WPF = 7.604 verificações**), num cluster isolado em loopback; o serviço PostgreSQL do computador não é usado. Os recursos acrescentados desde a alpha.5 não passaram por regressão completa no PostgreSQL 16 ou 18.

Instalação para todos os usuários (com elevação), instalação em VM Windows limpa e observações físicas de DPI, teclado e leitor de tela nas telas novas não são certificadas de novo por esta release. As telas novas foram exercitadas por testes automáticos, não à mão com mouse e teclado nesta fonte.

## Limites conhecidos

Somente Windows x64. Executável e instalador sem assinatura; o SmartScreen avisa. Sem MSI, atualização automática ou garantia de suporte em produção. Não se declara paridade completa com o SSMS. Resultados: ordenação, cópia e exportação valem para a página atual; as linhas depois da primeira página ficam num arquivo temporário, sem criptografia, até a próxima execução ou até a aba fechar; Results to Text mantém os limites de 10.000 linhas e 250.000 células, e Results to File não tem nenhum dos dois. O IntelliSense não tem Parameter Info nem Quick Info, e não conhece colunas de CTE, tabelas temporárias nem o `SET search_path` da sessão. Query Designer: `UNION` e `WITH` abrem apenas no painel SQL; Update, Delete e Insert Values aceitam uma tabela. Find Missing Foreign Keys é heurística: um nome igual é indício, não prova.

## Versões anteriores

v0.1.0-alpha.5 (08/11/2026, diagramas de banco, Query Designer, IntelliSense; Apache-2.0) continua disponível com seus arquivos e notas originais. v0.1.0-alpha.4 (07/11/2026, Activity Monitor em aba), v0.1.0-alpha.3 (02/11/2026, Query Statistics), v0.1.0-alpha.2 (01/11/2026, primeira release gerada por CI com atestado) e v0.1.0-alpha.1 (31/10/2026) continuam disponíveis com seus arquivos e notas originais, sob MIT.
