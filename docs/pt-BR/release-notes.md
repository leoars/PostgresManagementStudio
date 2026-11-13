# v0.1.0-alpha.5 — Windows x64

[English](../en/release-notes.md) | Português (Brasil)

Quinta pré-release pública do PGMS. É uma alpha para avaliação com dados sintéticos ou descartáveis. Rótulo de build: M1053. Os binários são gerados e atestados pelo mesmo [workflow de release](../../.github/workflows/release.yml) das versões anteriores. **Mudança de licença:** a partir desta versão, o trabalho próprio está sob Apache-2.0, com créditos no `NOTICE`; as versões até a alpha.4 continuam sob MIT.

## Em desenvolvimento (ainda não publicado)

Estas mudanças estão no `main` e farão parte da próxima pré-release; os downloads abaixo continuam sendo da v0.1.0-alpha.5.

- **Índices nos diagramas de banco.** Cada tabela de um diagrama tem uma seção **Indexes**, recolhida por padrão, com uma linha por índice quando aberta: método (GIN, GiST, SP-GiST, BRIN, Hash), colunas ou expressões da chave, classe de operadores quando não é a padrão (como `gin_trgm_ops`), `INCLUDE`, UNIQUE, EXCLUDE, parcial e inválido, com o predicado e o `CREATE INDEX` completo na dica. Um aviso marca a tabela com chave estrangeira que nenhum índice atende e abre **Find Unindexed Foreign Keys…**, que também chega nesta mudança: num banco do Object Explorer, lista essas chaves pelo catálogo e cria os índices a partir de um script revisado, de forma concorrente ou em uma transação. **Show Indexes** e **Refresh** ficam no menu do diagrama. Cada gravação escreve os índices de cada tabela na definição do diagrama para ferramentas e agentes de IA que a leem; diagramas antigos abrem sem mudança. Só leitura: índices continuam sendo criados em **Indexes/Keys…**. O SSMS 22.11 não mostra índices no diagrama. Sugestão de um usuário do PGMS.
- **Descrições nos diagramas de banco e exportação para IA.** Tabela, coluna e relação de um diagrama podem ter descrição, que é o `COMMENT ON` do objeto no PostgreSQL, então qualquer ferramenta que lê o catálogo a enxerga. Edite em **Description Property** pelo menu da tabela, da coluna ou da relação (ou clique no pequeno ícone de página que marca o item descrito, com o texto na dica); **Save** aplica depois da revisão do script, como toda mudança do diagrama. Se você não é o dono da tabela, a descrição fica no diagrama como não aplicada e o PGMS mostra o script para enviar ao dono. Cada gravação guarda uma cópia das descrições na definição do diagrama (formato 3); quando o banco mudou alguma depois disso, o diagrama mostra o texto do banco e um ícone laranja. **Export for AI…** escreve as tabelas do diagrama em Markdown (colunas, chaves, relações, índices e descrições, em ordem fixa, com uma instrução para o agente no topo) para copiar ou salvar, para um system prompt, um `CLAUDE.md` ou um `AGENTS.md`. A função opcional `pgms.diagram_markdown(name)`, criada só depois de você revisar o script, devolve o mesmo texto a um agente conectado ao banco. Veja [Usando diagramas com agentes de IA](user-guide.md#usando-diagramas-com-agentes-de-ia). No SSMS 22.11 as descrições são editadas na janela Properties; os ícones, a cópia e a exportação são acréscimos do PGMS. Sugestão de um usuário do PGMS.

## O que mudou desde a v0.1.0-alpha.4

- **Diagramas de banco.** Diagramas como os do SSMS, salvos no servidor: adicionar tabelas e tabelas relacionadas, arrastar uma coluna para criar chave estrangeira, Arrange Tables, zoom, modos de exibição, anotações, tabelas novas e edição de colunas dentro do diagrama. Alterações no banco aparecem antes como script SQL.
- **Query Designer.** **Query ▸ Design Query in Editor** (`Ctrl+Shift+Q`) com painéis Diagram, Criteria e SQL sincronizados, junções pelas chaves estrangeiras e Change Type (Update, Delete, Insert Values, Insert Results, Make Table). Ele escreve SQL no editor e nunca executa.
- **IntelliSense.** `Ctrl+Space` e `Ctrl+J` oferecem o que cabe na posição: tabelas, views e funções de tabela depois de `FROM`, buscadas em todos os schemas de usuário; junções inteiras pelas chaves estrangeiras depois de `JOIN`; colunas por alias. A busca ignora acentos.
- **Table Designer e View Designer** em abas de documento, com script de alteração para revisão.
- **Planos de execução gráficos.** Planos estimados e reais desenhados com operadores e setas proporcionais, propriedades, Find Node, zoom e abertura de plano salvo.
- **Resultados mais rápidos.** A thread que lê o servidor não espera mais a tela, na grade e em Results to Text. A grade não corta mais em 10.000 linhas ou 250.000 células: resultados maiores aparecem em páginas de 100.000 linhas, configurável em Query Options.
- **Results to Text e to File, Query Options e menu Query:** opções por aba com padrões em Tools ▸ Options, Parse, Client Statistics e comandos de Connection.
- **Cancelar chega ao servidor** mesmo com uma instrução seguinte do script em execução, e não arrisca mais derrubar a conexão no meio da leitura.
- **Gerar SQL com IA** com Anthropic, OpenAI ou um Ollama local, usando a sua própria chave. O pedido e a estrutura do banco vão para o provedor que você configurar; dados das tabelas nunca são lidos nem enviados.
- **Configure Server TLS.** Assistente que instala um certificado e liga o TLS, restaurando a configuração anterior se algo falhar. Exige conexão de superusuário a um servidor em Windows.
- **Object Explorer e administração.** Standard Reports, Registered Servers, Start/Stop/Restart do serviço local, Edit Top e Select Top, Find Missing Foreign Keys e menus de contexto por tipo de nó.
- **About** agora mostra o autor e os links do projeto.

## Downloads

Em [esta release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.5):

| Arquivo | Finalidade |
| --- | --- |
| `PGMS-0.1.0-alpha.5-Setup-x64.exe` | Instalador gráfico (inglês/pt-BR). Usuário atual sem administrador, ou todos os usuários com elevação. Não exige PowerShell nem runtime .NET separado. |
| `PGMS-0.1.0-alpha.5-win-x64.zip` | Aplicativo portátil com runtime .NET/WPF. Extraia a pasta inteira e execute PGMS.exe. Inclui os scripts PowerShell 7 gerenciados. |
| `SHA256SUMS.txt` | SHA256 de cada arquivo desta release. Hashes conferem correspondência, não autoria. |
| `package-manifest.json` | Manifesto SHA256 de cada arquivo dentro do ZIP, conferido por `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | Estas notas. |

## Confira o que você baixou

Os binários não têm assinatura de código, então o Windows SmartScreen pode avisar na primeira execução. Antes de escolher **Executar assim mesmo**, confira o checksum e a origem:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.5-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare os hashes com `SHA256SUMS.txt`. O segundo comando exige o [GitHub CLI](https://cli.github.com/) e confirma que o arquivo foi gerado pelo workflow de release deste repositório no commit da tag; rode também para o ZIP. A assinatura de código pelo programa open source da SignPath Foundation está planejada, sem data prometida.

## Escopo

Editor SQL com IntelliSense e sessões independentes, Object Explorer, Results em páginas e Results to Text/File, exportação CSV/JSON, diagramas de banco e designers, planos gráficos, administração PostgreSQL revisada, identidade/privilégios, Activity Monitor, estatísticas de consultas e backup/restore lógico. PostgreSQL 16 a 18 e ferramentas cliente da mesma versão principal são necessários para operações no banco e backup/restore; servidor e ferramentas não acompanham o pacote. O SSMS não é exigido nem distribuído.

## Validação

Comportamento do aplicativo: a fonte M1053 passou na regressão completa deste repositório no PostgreSQL 17.11 (**2.834 de integração + 4.337 WPF = 7.171 verificações**), num cluster isolado em loopback; o serviço PostgreSQL do computador não é usado. Os recursos acrescentados desde a alpha.4 não passaram por regressão completa no PostgreSQL 16 ou 18.

Instalação para todos os usuários (com elevação), instalação em VM Windows limpa e observações físicas de DPI, teclado e leitor de tela nas telas novas não são certificadas de novo por esta release. As telas novas foram exercitadas por testes automáticos, não à mão com mouse e teclado nesta fonte.

## Limites conhecidos

Somente Windows x64. Executável e instalador sem assinatura; o SmartScreen avisa. Sem MSI, atualização automática ou garantia de suporte em produção. Não se declara paridade completa com o SSMS. Resultados: ordenação, cópia e exportação valem para a página atual; as linhas depois da primeira página ficam num arquivo temporário, sem criptografia, até a próxima execução ou até a aba fechar; Results to Text mantém os limites de 10.000 linhas e 250.000 células, e Results to File não tem nenhum dos dois. O IntelliSense não tem Parameter Info nem Quick Info, e não conhece colunas de CTE, tabelas temporárias nem o `SET search_path` da sessão. Query Designer: `UNION` e `WITH` abrem apenas no painel SQL; Update, Delete e Insert Values aceitam uma tabela. Find Missing Foreign Keys é heurística: um nome igual é indício, não prova.

## Versões anteriores

v0.1.0-alpha.4 (07/11/2026, Activity Monitor em aba), v0.1.0-alpha.3 (02/11/2026, Query Statistics), v0.1.0-alpha.2 (01/11/2026, primeira release gerada por CI com atestado) e v0.1.0-alpha.1 (31/10/2026) continuam disponíveis com seus arquivos e notas originais, sob MIT.
