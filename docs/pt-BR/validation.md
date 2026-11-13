# Validação e limites

[English](../en/validation.md) | Português (Brasil)

Pré-release atual: v0.1.0-alpha.6, rótulo M1062 (acrescenta índices, descrições e Export for AI nos diagramas, Query History, Script as, avisos e sugestão de índice nos planos e a escolha de tabelas do SQL assistido por IA em bancos grandes à fonte M1053 da alpha.5; compilada pelo workflow de release no GitHub Actions com atestado de proveniência de build). Marcos são identificadores internos, não garantia de identidade binária de uma recompilação local. A importação pública inicial usou M1002. Histórico operacional e provas brutas privados não são incluídos intencionalmente.

A implementação importada passou anteriormente em 5.187 checks automatizados (2.115 integração PostgreSQL e 3.072 WPF), além de abertura de pacote, preservação de perfil instalado e recuperação de três rascunhos entre rollback/reinício. São observações históricas, não testes novos a cada revisão. Reproduza o comportamento atual pelos scripts públicos e informe ambiente/resultados reais. As verificações da revisão pública inicial são registradas abaixo após execução.

Limites conhecidos: Windows x64; PostgreSQL 16 a 18 com ferramentas da mesma versão principal, regressão completa no 17.11 e suítes focadas no 16.15/18.6; 15 e anteriores não suportados. Sem paridade integral SSMS. Linhas SQL enormes podem pausar renderização. Resultados/recuperação têm limites. Cancelar não comprova rollback. DPAPI depende do usuário. Pacote sem assinatura/MSI/atualização automática; SmartScreen não certificado. DPI100/125/150/175% exercitados internamente;200% aceito sem execução. Reaparecimento automático após desconexão, DPAPI entre usuários e nova prova desta importação em Windows limpo não certificados. Observações de clipboard/teclado/leitor são históricas, não reproduzidas por checks WPF comuns.

```powershell
./scripts/Build.ps1 -Configuration Release
./scripts/Test-Integration.ps1
./scripts/Test-Package.ps1
./scripts/Test-Installation.ps1
./scripts/Test-PublicDocumentation.ps1
```

O harness cria cluster descartável em loopback no diretório artifacts ignorado deste checkout, exclusivamente loopback (porta 55439 por padrão, configurável por -TestPort), e o encerra ao final. Nunca use bancos pessoais ou de produção nesses testes. Veja [desenvolvimento](development.md).

## Checks da fonte pública inicial — 31/10/2026

Build Release passou sem avisos/erros. Foram 157 checks focados de arquivos desktop, 22 de pacote e 46 de instalação em fixtures. A documentação foi conferida em 12 pares de idiomas e links locais. Pacote self-contained local passou em manifesto/licença/guias bilíngues, carregou runtime próprio e fechou normalmente com saída0/estado isolado. Nenhuma release binária foi enviada.

A regressão PostgreSQL completa foi tentada, mas parou antes de iniciar o servidor porque Windows negou bind na porta isolada 55439 (erro de acesso ao socket). Uma prova separada em loopback reproduziu; porta dinâmica funcionou. Não se atribui repetição dos 5.187 checks históricos a esta importação. O cluster descartável não estava ativo ao final. Não foram alterados firewall ou bancos do usuário. Gitleaks não encontrou alertas na fonte selecionada, sem exceções históricas.

## v0.1.0-alpha.1 / M1008 — 31/10/2026

Execução completa nova passou 2.115 integração +3.072 WPF =5.187 checks no PostgreSQL 17.11, com cluster descartável em 127.0.0.1:55441; fonte do aplicativo inalterada durante a execução, cluster encerrado e variável anterior de porta restaurada. Onze checks do parser de porta e duas recusas de porta inválida no runner passaram separadamente. A porta padrão estava ocupada; a configuração foi adicionada somente aos testes, sem alterar firewall ou encerrar processos alheios.

A primeira tentativa integral encontrou corrida no arquivo de prontidão de um teste: o pai observou o arquivo antes de o escritor fechá-lo. Filhos de teste agora publicam o sinal renomeando temporário já fechado. Recortes de processos passaram 26+20, seguidos da regressão integral aprovada acima; a falha anterior não foi contada como aprovação. Essas mudanças de harness não alteram o comportamento de banco do aplicativo distribuído. Metadados/rótulo e empacotamento mudaram para esta pré-release.

Resultados do pacote e instalador estão nas [notas da versão](release-notes.md). Provas históricas PostgreSQL 18/físicas/VM não foram reclassificadas como novas desta versão. Sem nova certificação Windows limpo ou SmartScreen.

## v0.1.0-alpha.3 / M1022 — 02/11/2026

Uma execução completa passou em 2.236 checks de integração + 3.315 WPF = 5.551 no PostgreSQL 17.11. O cluster descartável em loopback agora inicia com `shared_preload_libraries=pg_stat_statements`; o serviço do host não é tocado, e o cluster foi encerrado ao final. A suíte focada `Test-Integration.ps1 -QueryStatisticsOnly` passou em 90 checks de integração + 224 WPF. A integração cobre detecção sem a extensão (nada é instalado), roles comuns contra `pg_read_all_stats`, filtros/ordem no servidor e a sentinela de 1.000 linhas, resets por statement e total recusados ou autorizados pelo PostgreSQL, linhas de base e janelas, planos genéricos e detecção de placeholders entre literais, comentários e dollar quotes com a sessão mantida aberta. O WPF cobre carga, cancelamento sob lock real, seleção preservada, ordenação pelo cabeçalho, gráfico, seletor de janela, preferências gravadas (inclusive arquivo corrompido preservado), nomes acessíveis das linhas e layout no tamanho mínimo.

Durante o desenvolvimento, a janela foi comparada com o Query Store do SSMS 22.11 sob carga sintética idêntica, contra SQL Server LocalDB e o cluster PostgreSQL descartável. Os dois listaram os mesmos statements com contagens iguais. Diferenças encontradas na época: uma janela em vez de sete relatórios do Query Store; contadores acumulados do servidor em vez de intervalos de tempo por banco; sem gráfico, plano, leituras lógicas ou ordenação pelo cabeçalho; registros inteiros expostos como nome das linhas a leitores de tela. Ordenação pelo cabeçalho, métricas de I/O e blocos, planos genéricos, gráfico top-N, janelas de tempo e nomes curtos de linha foram acrescentados depois. Diferenças que permanecem: uma janela em vez de relatórios separados, janelas calculadas subtraindo amostras em vez de intervalos armazenados, e sem tempo de CPU por statement, que o PostgreSQL não registra. Observações físicas de DPI e leitor de tela desta janela não recebem nova certificação.

## v0.1.0-alpha.6 / M1062 — 13/11/2026

Uma execução completa passou com 3.018 de integração + 4.586 WPF = 7.604 verificações no PostgreSQL 17.11, num cluster descartável em loopback com `pg_stat_statements` pré-carregado, na primeira tentativa. A regressão completa não foi repetida no PostgreSQL 16 ou 18. As telas novas foram exercitadas por testes automáticos, não à mão com mouse e teclado. A escolha de tabelas do Gerar SQL com IA em bancos grandes não foi executada com um provedor real, e nenhum agente de IA real leu o Markdown do Export for AI.

## v0.1.0-alpha.5 / M1053 — 08/11/2026

Uma execução completa passou com 2.834 de integração + 4.337 WPF = 7.171 verificações no PostgreSQL 17.11, num cluster descartável em loopback com `pg_stat_statements` pré-carregado. A regressão completa não foi repetida no PostgreSQL 16 ou 18. As telas novas foram exercitadas por testes automáticos, não à mão com mouse e teclado.

## v0.1.0-alpha.4 / M1030 — 04/11/2026

Uma execução completa passou em 2.318 checks de integração + 3.429 WPF = 5.747 no PostgreSQL 17.11, no mesmo cluster descartável em loopback com `pg_stat_statements` pré-carregado; o cluster foi encerrado ao final. No checkout público, a suíte focada `Test-Integration.ps1 -ActivityOnly` passou em 71 checks de integração + 152 WPF e `-PartitionTablesOnly` em 113 de integração + 101 WPF. Sobre a mesma fonte do aplicativo, no repositório de desenvolvimento, a suíte do Activity Monitor passou em 71 checks de integração + 152 WPF no PostgreSQL 16.15 e no 18.6, com ferramentas da mesma versão principal; a do Query Statistics passou nas duas versões sobre a fonte M1029, e o código dela não mudou depois. A integração cobre cadeias de bloqueio e o head blocker entre databases, páginas por PID dentro de um mesmo snapshot, métricas do Overview a partir de `pg_stat_io` e `pg_stat_database`, esperas amostradas sem `pg_sleep`, a variação do `pg_stat_statements` entre atualizações, detach concorrente com suas pré-condições e `FINALIZE`, e criação, attach e detach de tabelas subparticionadas. O WPF cobre a aba de documento e seu reaproveitamento, painéis, gráficos, pausa/retomada, intervalos de atualização, menus da linha e do painel, Session Details e layout no tamanho mínimo.

Durante o desenvolvimento, o Activity Monitor foi comparado duas vezes com o do SSMS 22.11, sob a mesma carga de bloqueio contra SQL Server LocalDB e o cluster PostgreSQL descartável, inclusive com cliques direitos reais no menu do Overview. As diferenças encontradas foram corrigidas, exceto estas, mantidas por decisão ou pelo que o PostgreSQL oferece: duplo clique numa sessão abre o Session Details (no SSMS não faz nada); a seleção acompanha a sessão quando a lista é reordenada (o SSMS mantém a posição); o Overview mostra sessões ativas onde o SSMS mostra tempo de processador; Data File I/O é por database porque o PostgreSQL não tem estatísticas por arquivo; Resource Waits é amostrado, não acumulado. Nada foi testado com mais de 1.000 sessões, e observações físicas de DPI e leitor de tela das novas telas não recebem nova certificação.