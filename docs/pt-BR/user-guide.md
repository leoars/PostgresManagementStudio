# Guia de uso

[English](../en/user-guide.md) | Português (Brasil)

## Conectar e consultar

Abra **New Query** (`Ctrl+N`) e **Connect**. Informe host, porta, banco, role e TLS. O padrão VerifyFull exige certificado confiável e hostname correspondente. **Test Connection** verifica a conexão sem salvar perfil. Salve o perfil explicitamente; armazenar senha é opcional e protegido por DPAPI Windows. Cada aba tem sua sessão PostgreSQL. Trocar de banco exige conexão, não o comando SQL Server `USE`. Não há tradução de T-SQL.

Use `F5` para executar seleção ou documento e **Cancel** para solicitar cancelamento. Cancelar não comprova rollback de comando ou COMMIT no servidor. Confira o resultado antes de repetir uma escrita. Results/Messages apresentam resultados e diagnóstico. Read timeout, configurações da sessão e planos têm controles próprios. **Analyze Plan** executa SQL e exige revisão; use somente quando quiser essa execução. SQL com placeholders do tipo `$1` não executa sem valores de parâmetro: o PGMS recusa antes de enviar, mantém a sessão aberta e aponta o placeholder. `PREPARE` e corpos de funções/procedures ficam de fora da recusa, e **Estimated Plan** passa a usar um plano genérico.

Resultados têm limites: 10.000 linhas, 100 conjuntos, 256 colunas por resultado, 4.096 colunas somadas e 250.000 células, além dos limites de texto. Avisos explicam dados omitidos; comandos SQL posteriores podem continuar. CSV/JSON exporta dados retidos exibidos/selecionados, não o banco completo. `Ctrl+C` copia com cabeçalhos; `Ctrl+Shift+C` omite cabeçalhos. A cópia limita 250.000 posições/4Mi unidades UTF-16 e distingue NULL de texto vazio.

## Inspecionar e administrar

Object Explorer carrega pastas sob demanda, filtra e inspeciona objetos. Operações suportadas oferecem preview SQL e Apply explícito. Script não inclui todas as proteções de concorrência do Apply. Use os privilégios PostgreSQL mínimos necessários; o PGMS não contorna autorização do servidor. Confira banco/schema/objeto e role antes de aplicar.

Identity e sequences distinguem START configurado de RESTART. Reiniciar pode repetir valores existentes. O diálogo **Partitions** cria, anexa e desanexa partições. Uma filha nova pode ser ela própria particionada (**Sub-partition by**, com chave de colunas do pai), e uma tabela particionada pode ser anexada ou desanexada com sua subárvore. **Detach concurrently** executa `DETACH PARTITION … CONCURRENTLY` fora de transação, sem bloquear consultas; é recusado para partição DEFAULT ou quando já há outro detach pendente, e um detach pendente é concluído com `FINALIZE`. Privilégios, delegação, CASCADE e reconciliação de COMMIT têm regras próprias de revisão e concorrência. Veja o [índice da documentação](../README.md) para os comportamentos suportados.

## Diagramas de banco de dados

No Object Explorer, clique com o botão direito em **Database Diagrams**, dentro de um banco, e escolha **New Database Diagram**. Na primeira vez, o PGMS pede para criar seus objetos de suporte (schema `pgms`). Adicione tabelas com **Add Table** ou **Add Related Tables**, arraste uma coluna sobre outra para criar uma chave estrangeira e use **Arrange Tables** para organizar. Ao salvar, o PGMS mostra o script SQL de qualquer alteração no banco para revisão.

**Índices.** Cada tabela mostra uma seção **Indexes** abaixo das colunas, recolhida por padrão para que diagramas já organizados mantenham o espaçamento: o cabeçalho traz a quantidade e os métodos que não são b-tree (por exemplo `Indexes (3) · GIN`), e a dica dele lista todos os índices. Clique no cabeçalho para ver uma linha por índice: nome, método quando não é b-tree (Hash, GIN, GiST, SP-GiST, BRIN), colunas ou expressões da chave, a classe de operadores quando não é a padrão (como `gin_trgm_ops`, `jsonb_path_ops` ou `text_pattern_ops`), colunas `INCLUDE` e UNIQUE, EXCLUDE, parcial (partial) ou inválido (invalid). A dica da linha traz o predicado de um índice parcial e o `CREATE INDEX` completo. Uma chave dourada marca a chave primária, uma chave prateada uma constraint UNIQUE ou de exclusão, e uma árvore um índice comum. Um triângulo de aviso no título marca a tabela com chave estrangeira que nenhum índice atende, pela mesma regra de **Find Unindexed Foreign Keys…**; clique nele, ou use o item do menu da tabela, para abrir essa janela. **Show Indexes**, no menu do diagrama, liga e desliga as seções e o aviso naquele diagrama, e **Refresh** lê de novo as tabelas e seus índices. A seção é só leitura: crie ou altere índices em **Indexes/Keys…** (duplo clique numa linha de índice) e nos outros diálogos. Toda gravação escreve os índices de cada tabela, lidos do catálogo dentro da transação da gravação, na definição do diagrama (`pgms.diagrams.definition`, formato 2), para que ferramentas e agentes de IA que leem o diagrama os vejam; mudanças feitas só no banco não marcam o diagrama como alterado e chegam à definição na próxima gravação. Diagramas salvos por versões anteriores abrem como estavam e passam ao formato novo na próxima gravação. O SSMS 22.11 não mostra índices no diagrama; esta seção é um acréscimo do PGMS.

## Query Designer

**Query ▸ Design Query in Editor** (`Ctrl+Shift+Q`) abre a instrução sob o cursor, ou a seleção, nos painéis Diagram, Criteria e SQL. **OK** substitui essa instrução no editor; nada é executado. Numa linha vazia, abre vazio e insere no cursor. `UNION` e `WITH` abrem apenas no painel SQL.

## IntelliSense

`Ctrl+Space` ou `Ctrl+J` abrem a lista; digitar um ponto abre sozinho. `Tab` aceita e `Esc` fecha. **Query ▸ IntelliSense Enabled** (`Ctrl+B`, `Ctrl+I`) desliga na aba, e **Edit ▸ IntelliSense ▸ Refresh Local Cache** (`Ctrl+Shift+R`) relê o catálogo depois de mudanças de schema. Depois de `FROM` e palavras parecidas, busca o nome da tabela em todos os schemas de usuário; depois de `JOIN`, oferece junções inteiras a partir das chaves estrangeiras.

## Resultados grandes

Um resultado na grade maior que **Query Options ▸ Results ▸ Grid ▸ Rows per result page** (100.000 por padrão) aparece em páginas, com uma barra abaixo da grade. As linhas depois da primeira página ficam num arquivo temporário até a próxima execução ou até a aba fechar. Ordenação, cópia e exportação valem para a página atual; **Results to File** grava todas as linhas.

## Activity Monitor

**View → Activity Monitor** (`Ctrl+Alt+A`) abre uma aba de documento para o servidor e a role conectados (`host:porta (role) - Activity Monitor`); abrir de novo ativa a mesma aba. Tem seis painéis recolhíveis, seguindo o layout do SSMS:

- **Overview**: gráficos de sessões ativas, sessões em espera, I/O do banco (`pg_stat_io`) e transações por segundo. O menu de contexto define o intervalo de atualização e pausa, retoma ou atualiza o monitor inteiro.
- **Processes**: sessões de `pg_stat_activity` com host, estado, idade da consulta e da transação, espera, **Blocked by PID** e **Head blocker**, filtros aplicados no servidor e páginas por PID. **Details…** abre a consulta mais recente da sessão; **Cancel Query** e **Terminate Session…** agem sobre a sessão selecionada.
- **Resource Waits**: categorias de espera amostradas a cada atualização. O PostgreSQL não guarda tempo de espera acumulado, então os valores são estimativas; esperas ociosas e `pg_sleep` ficam de fora.
- **Data File I/O**: leituras, escritas temporárias, tempo de leitura e acerto de buffer por database (`pg_stat_database`), não por arquivo: o PostgreSQL não tem estatísticas por arquivo.
- **Recent Expensive Queries**: a variação de `pg_stat_statements` entre atualizações; exige a extensão, como o Query Statistics.
- **Active Expensive Queries**: comandos em execução. **Edit Query Text** abre um deles numa aba de consulta; **Show Execution Plan** mostra o plano estimado sem executá-lo.

A atualização automática começa ligada, a cada 10 segundos, com intervalos de 1 segundo a 1 hora. **Cancel Refresh**, **Cancel Query** e **Terminate Session** são ações distintas; atualizar um snapshot não autoriza encerrar uma sessão. As páginas são leituras separadas, então uma sessão pode mudar de página, e a ordenação vale dentro da página carregada. Roles fora de `pg_read_all_stats` podem não ver consultas e esperas de outras roles.

## Estatísticas de consultas

**View → Query Statistics…** lê o `pg_stat_statements` do banco conectado: statements normalizados com chamadas, tempo de execução (total, médio, mínimo, máximo, desvio padrão), linhas, % de acerto em cache, blocos compartilhados e temporários, tempo de I/O e bytes de WAL. O propósito é parecido com o Query Store do SQL Server; não é cópia das telas ou relatórios dele.

O PGMS nunca carrega, cria ou configura a extensão. O servidor precisa de `shared_preload_libraries = 'pg_stat_statements'` (com reinício) e de `CREATE EXTENSION pg_stat_statements` em um banco; quando algo falta, uma faixa amarela diz o quê. Roles fora de `pg_read_all_stats` veem os statements de outros usuários sem o texto SQL, por decisão do PostgreSQL. O tempo de I/O só é registrado com `track_io_timing` ligado. O PostgreSQL não tem tempo de CPU por statement. A versão 1.10 da extensão (PostgreSQL 16) é lida sem `stats_since`: um statement removido e rastreado de novo durante uma janela de tempo pode ser contado a menos, e o status da janela avisa. Versões anteriores à 1.10 não são lidas; a faixa indica o `ALTER EXTENSION pg_stat_statements UPDATE` que um usuário privilegiado pode executar, e o PGMS não altera nada.

Filtros (banco, usuário, texto SQL, mínimo de chamadas) e ordenação rodam no servidor antes do limite de 1.000 linhas. Clicar nos cabeçalhos Total, Mean, Max, Calls, Rows ou de métricas recarrega nessa ordem; os demais cabeçalhos ordenam só as linhas carregadas. **Window** mostra valores acumulados desde o último reset, desde uma linha de base gravada, ou dos últimos 5/15/60 minutos, calculados subtraindo uma leitura anterior; as janelas deslizantes só amostram enquanto a janela está aberta, e mínimo/máximo não existem para uma janela. Um gráfico top-N ao lado da grade segue a ordem aplicada. **Show Plan** abre o statement selecionado numa nova aba de consulta com `EXPLAIN (GENERIC_PLAN)`, sem executá-lo nem vincular valores aos `$n`. **Reset Selected** e **Reset All** chamam `pg_stat_statements_reset` depois de uma confirmação com Não como padrão; o servidor decide se a role pode zerar. O auto-refresh é opcional e começa desligado. Filtros, opções do gráfico e linhas de base ficam gravados por alvo de conexão na pasta de estado do PGMS, sem senhas nem resultados SQL.

## Backup e restauração

Escolha ferramentas PostgreSQL compatíveis e arquivo/diretório de saída novo. Custom, tar, SQL e diretório oferecem as opções de conteúdo documentadas. Restore usa banco de destino existente; objetos/roles compatíveis e permissões precisam existir quando exigidos. Ownership e privilégios são escolhas explícitas. Backup em diretório oferece dump paralelo; restore continua serial. Operações globais cobrem roles/memberships/tablespaces, não backup físico do cluster.

Execute apenas scripts SQL confiáveis: scripts psql podem conter comandos locais, reconexões e COMMIT. Transação única não anula esses comandos. Cancelamento pode chegar após commit. Chaves cliente criptografadas, CR/LF nas senhas das ferramentas e TEMP não ASCII têm restrições documentadas. Preserve o backup original e confira o destino após falhas. O [guia de distribuição](distribution.md) descreve ferramentas, permissões e requisitos do pacote.

## Salvar e recuperar

Salve arquivos SQL explicitamente. Recuperação em background mantém rascunhos criptografados com limites e pode reabri-los desconectados após interrupção. Revise abas recuperadas e use **Save As**. Isso não é sistema de backup nem prova de persistência de cada última tecla. Alterações externas conflitantes no arquivo são recusadas. Feche o app antes de copiar estado local; DPAPI depende do usuário Windows. Builds anteriores a M934 não leem o contêiner de recuperação mais recente. Veja a [validação](validation.md) para a recuperação entre versões realmente testada.
