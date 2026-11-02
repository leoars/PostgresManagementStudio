# Guia de uso

[English](../en/user-guide.md) | Português (Brasil)

## Conectar e consultar

Abra **New Query** (`Ctrl+N`) e **Connect**. Informe host, porta, banco, role e TLS. O padrão VerifyFull exige certificado confiável e hostname correspondente. **Test Connection** verifica a conexão sem salvar perfil. Salve o perfil explicitamente; armazenar senha é opcional e protegido por DPAPI Windows. Cada aba tem sua sessão PostgreSQL. Trocar de banco exige conexão, não o comando SQL Server `USE`. Não há tradução de T-SQL.

Use `F5` para executar seleção ou documento e **Cancel** para solicitar cancelamento. Cancelar não comprova rollback de comando ou COMMIT no servidor. Confira o resultado antes de repetir uma escrita. Results/Messages apresentam resultados e diagnóstico. Read timeout, configurações da sessão e planos têm controles próprios. **Analyze Plan** executa SQL e exige revisão; use somente quando quiser essa execução. SQL com placeholders do tipo `$1` não executa sem valores de parâmetro: o PGMS recusa antes de enviar, mantém a sessão aberta e aponta o placeholder. `PREPARE` e corpos de funções/procedures ficam de fora da recusa, e **Estimated Plan** passa a usar um plano genérico.

Resultados têm limites: 10.000 linhas, 100 conjuntos, 256 colunas por resultado, 4.096 colunas somadas e 250.000 células, além dos limites de texto. Avisos explicam dados omitidos; comandos SQL posteriores podem continuar. CSV/JSON exporta dados retidos exibidos/selecionados, não o banco completo. `Ctrl+C` copia com cabeçalhos; `Ctrl+Shift+C` omite cabeçalhos. A cópia limita 250.000 posições/4Mi unidades UTF-16 e distingue NULL de texto vazio.

## Inspecionar e administrar

Object Explorer carrega pastas sob demanda, filtra e inspeciona objetos. Operações suportadas oferecem preview SQL e Apply explícito. Script não inclui todas as proteções de concorrência do Apply. Use os privilégios PostgreSQL mínimos necessários; o PGMS não contorna autorização do servidor. Confira banco/schema/objeto e role antes de aplicar.

Identity e sequences distinguem START configurado de RESTART. Reiniciar pode repetir valores existentes. Privilégios, delegação, CASCADE e reconciliação de COMMIT têm regras próprias de revisão e concorrência. Activity Monitor distingue **Cancel Refresh**, **Cancel Query** e **Terminate Session**. Atualizar um snapshot não autoriza encerrar uma sessão. Veja o [índice da documentação](../README.md) para os comportamentos suportados.

## Estatísticas de consultas

**View → Query Statistics…** lê o `pg_stat_statements` do banco conectado: statements normalizados com chamadas, tempo de execução (total, médio, mínimo, máximo, desvio padrão), linhas, % de acerto em cache, blocos compartilhados e temporários, tempo de I/O e bytes de WAL. O propósito é parecido com o Query Store do SQL Server; não é cópia das telas ou relatórios dele.

O PGMS nunca carrega, cria ou configura a extensão. O servidor precisa de `shared_preload_libraries = 'pg_stat_statements'` (com reinício) e de `CREATE EXTENSION pg_stat_statements` em um banco; quando algo falta, uma faixa amarela diz o quê. Roles fora de `pg_read_all_stats` veem os statements de outros usuários sem o texto SQL, por decisão do PostgreSQL. O tempo de I/O só é registrado com `track_io_timing` ligado. O PostgreSQL não tem tempo de CPU por statement.

Filtros (banco, usuário, texto SQL, mínimo de chamadas) e ordenação rodam no servidor antes do limite de 1.000 linhas. Clicar nos cabeçalhos Total, Mean, Max, Calls, Rows ou de métricas recarrega nessa ordem; os demais cabeçalhos ordenam só as linhas carregadas. **Window** mostra valores acumulados desde o último reset, desde uma linha de base gravada, ou dos últimos 5/15/60 minutos, calculados subtraindo uma leitura anterior; as janelas deslizantes só amostram enquanto a janela está aberta, e mínimo/máximo não existem para uma janela. Um gráfico top-N ao lado da grade segue a ordem aplicada. **Show Plan** abre o statement selecionado numa nova aba de consulta com `EXPLAIN (GENERIC_PLAN)`, sem executá-lo nem vincular valores aos `$n`. **Reset Selected** e **Reset All** chamam `pg_stat_statements_reset` depois de uma confirmação com Não como padrão; o servidor decide se a role pode zerar. O auto-refresh é opcional e começa desligado. Filtros, opções do gráfico e linhas de base ficam gravados por alvo de conexão na pasta de estado do PGMS, sem senhas nem resultados SQL.

## Backup e restauração

Escolha ferramentas PostgreSQL compatíveis e arquivo/diretório de saída novo. Custom, tar, SQL e diretório oferecem as opções de conteúdo documentadas. Restore usa banco de destino existente; objetos/roles compatíveis e permissões precisam existir quando exigidos. Ownership e privilégios são escolhas explícitas. Backup em diretório oferece dump paralelo; restore continua serial. Operações globais cobrem roles/memberships/tablespaces, não backup físico do cluster.

Execute apenas scripts SQL confiáveis: scripts psql podem conter comandos locais, reconexões e COMMIT. Transação única não anula esses comandos. Cancelamento pode chegar após commit. Chaves cliente criptografadas, CR/LF nas senhas das ferramentas e TEMP não ASCII têm restrições documentadas. Preserve o backup original e confira o destino após falhas. O [guia de distribuição](distribution.md) descreve ferramentas, permissões e requisitos do pacote.

## Salvar e recuperar

Salve arquivos SQL explicitamente. Recuperação em background mantém rascunhos criptografados com limites e pode reabri-los desconectados após interrupção. Revise abas recuperadas e use **Save As**. Isso não é sistema de backup nem prova de persistência de cada última tecla. Alterações externas conflitantes no arquivo são recusadas. Feche o app antes de copiar estado local; DPAPI depende do usuário Windows. Builds anteriores a M934 não leem o contêiner de recuperação mais recente. Veja a [validação](validation.md) para a recuperação entre versões realmente testada.
