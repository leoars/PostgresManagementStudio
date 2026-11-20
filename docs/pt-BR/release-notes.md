# v0.1.0-beta.1 — Windows x64

[English](../en/release-notes.md) | Português (Brasil)

Primeiro beta do PGMS, ainda publicado como pré-release. As funções desta versão estão completas e ela se destina a trabalho real; ainda pode ter defeitos, então mantenha backups e relate o que encontrar. Rótulo de build: M1145. Os binários são gerados e atestados pelo mesmo [workflow de release](../../.github/workflows/release.yml) das versões anteriores. O trabalho original está sob Apache-2.0, com créditos no `NOTICE`; as versões até a alpha.4 continuam sob MIT.

> **Não testado numa máquina Windows limpa.** Este beta foi instalado e executado só no computador do mantenedor, que já tem PostgreSQL, SSMS e ferramentas de desenvolvimento. Ele não foi testado numa máquina virtual Windows limpa nem por outro usuário Windows sem direitos de administrador. Se o instalador ou a primeira abertura falhar na sua máquina, por favor [relate](https://github.com/leoars/PostgresManagementStudio/issues/new?template=bug_report.yml).

## Destaques

- **Interface em 8 idiomas.** English, Português (Brasil), Español, Deutsch, Italiano, Français, 한국어 e 日本語. Por padrão o PGMS segue o idioma de exibição do Windows (inglês quando o Windows usa outro); **Tools ▸ Options ▸ Environment ▸ International Settings** escolhe outro, aplicado quando o PGMS abre de novo. Os nomes das pastas do Object Explorer e os nomes de comando da página Keyboard ficam em inglês, como no SSMS; SQL, dados e mensagens do servidor nunca são traduzidos. **As traduções ainda não foram revisadas por falantes nativos**: por favor [relate qualquer problema de tradução](https://github.com/leoars/PostgresManagementStudio/issues/new?template=translation.yml).
- **Assistente de IA** (sem mudança desde a alpha.7): **Query ▸ Generate SQL with AI…** (`Ctrl+I`) com pedidos de continuação, e **Explain**, **Fix** e **Optimize SQL with AI**. Funciona com Anthropic, OpenAI ou um Ollama local, com a sua chave. O pedido, a estrutura do banco (nomes, tipos, chaves, comentários) e, no **Fix**, o SQLSTATE e a mensagem principal do último erro vão ao provedor configurado; dados das tabelas nunca são lidos nem enviados, nada é enviado antes de **Generate**, e o PGMS nunca executa o SQL por conta própria.
- **Configure Server TLS** (sem mudança): configuração guiada que instala um certificado e liga o TLS, restaurando a configuração anterior se falhar. Exige conexão de superusuário a um servidor em Windows.
- **Temas como no SSMS.** **Tools ▸ Theme** lista os 13 temas do SSMS na ordem dele (Cool Breeze, a aparência de sempre do PGMS, é o padrão; Light é o cinza do SSMS; Dark; e os temas coloridos), mais **Use system setting**.
- **Planos de execução.** O menu do plano ganha **Show Execution Plan JSON**, **Show Execution Plan as Text** (o layout em texto do `EXPLAIN`, montado sem executar a consulta de novo) e **Compare Showplan**, que compara o plano com um arquivo de plano salvo ou com o plano anterior da mesma aba. Os operadores que diferem ficam sombreados nos dois desenhos (âmbar: outros valores; rosa: só num plano), com uma grade Differences.
- **PostGIS.** Valores `geometry` e `geography` aparecem como texto EWKT (`SRID=4326;POINT(1 2)`) na grade, no Results to Text, na cópia e na exportação; o Table Designer aceita tipos como `geometry(Point,4326)` e lista os tipos das extensões instaladas.
- **Aviso de nova versão.** Alguns segundos depois de abrir, os pacotes de release perguntam à API do GitHub se há versão mais nova (o GitHub vê o seu endereço IP, como em qualquer acesso à web) e mostram **Update available** na barra de título, com Open the release page e Skip This Version. **Help ▸ Check for Updates** pergunta na hora. Nada é baixado nem instalado. Desligue em **Tools ▸ Options ▸ Environment ▸ Product Updates**.

## O que mudou desde a v0.1.0-alpha.7

**Opções**
- Páginas novas, cada opção com efeito real ou mostrada desabilitada com o motivo: Environment ▸ **AutoRecover** (liga/desliga, intervalo em minutos, vida útil em dias), **Documents** (arquivos mudados fora do PGMS, arquivos somente leitura, Open a partir da pasta do arquivo ativo, finais de linha mistos, salvar em segundo plano, codificação ao salvar), **Tabs**, **Task List** (só leitura) e **Windows**; as sete páginas do **SQL Formatter** (caixa das palavras-chave, Format on save, alinhamento, recuo, listas em várias linhas, quebras de linha, espaçamento); Designers ▸ **Table Options** (tempo limite ao salvar, aviso de tabela sem chave primária); Object Explorer ▸ **General** (Hide system objects) e **Commands** (confirmar renomeações); Query Execution ▸ **Live Query Statistics** (intervalo de leitura); Text Editor ▸ **Transact-SQL** (quebra de linha, números de linha, tabulação, IntelliSense para janelas novas).
- **Padrões que seguem o SSMS:** abas novas abrem primeiro, depois das fixadas; consultas não salvas têm cópia a cada 5 minutos; esquemas de sistema (`pg_catalog`, `information_schema`) e bancos modelo aparecem no Object Explorer, nas pastas **System Schemas** e **System Databases**, a primeira pasta em Schemas e em Databases (com Hide system objects ligado, as pastas e seus objetos somem); uma instrução do Table Designer que passe de 3600 segundos interrompe o salvamento e nada é alterado; o Live Query Statistics lê a cada 250 ms; o Format SQL escreve um espaço entre o tipo e os parâmetros (`VARCHAR (20)`). Consultas recuperadas continuam voltando na ordem salva. Qualquer um desses pode ser mudado de volta em Tools ▸ Options.
- **Results to Grid ▸ Show multiple result sets stacked in one tab** (desligada por padrão: uma aba por conjunto de resultados).

**Editor e janelas**
- **Edit ▸ Paste Special** ▸ Format SQL (cola formatado, um Undo) e Paste with AI…; **Edit ▸ IntelliSense ▸ Switch Completion Mode** (`Ctrl+Alt+Space`); **File ▸ New ▸ File…** (`Ctrl+Alt+N`); **Tools ▸ Code Snippets Manager…** (`Ctrl+K`, `Ctrl+B`).
- **View ▸ Navigate Backward/Forward** (`Ctrl+-`, `Ctrl+Shift+-`), **All Windows** em Full Screen (`Shift+Alt+M`), **Window ▸ Pin Tab** (abas fixadas continuam fixadas quando recuperadas ou reabertas com Restore Closed Tab) e Reset Current Object Explorer Details View; as barras Standard e SQL Editor têm os botões do SSMS na ordem dele.
- As caixas de mensagem são desenhadas pelo PGMS, com os botões no idioma do PGMS, as teclas de sempre (Enter, Esc, letras de acesso), o ícone e o som do sistema, e `Ctrl+C` para copiar o texto.
- Títulos de abas e mensagens compostas são traduzidos; nomes de arquivos e consultas, não.

**Desempenho e acessibilidade**
- As grades de resultados desenham cada célula com um elemento e expõem menos objetos de automação: rolar e carregar resultados grandes ficou mais rápido. A grade se preenche nos mesmos passos do SSMS (30, 100, 10.000 linhas e depois o fim).
- Quando o PGMS reabre depois de um fechamento inesperado, só a aba recuperada ativa monta o editor na hora; as outras montam quando mostradas.
- Correções de teclado e leitor de tela vindas de uma passada com teclas reais: foco e tamanho das janelas de ferramentas, Tab não fica mais preso nas barras (as barras ficam fora da ordem de Tab, como no SSMS), painéis auto-ocultos e divisores fora da ordem de Tab, nomes lidos pelo Narrador no Connect e em caixas de combinação editáveis. Toda janela agora cabe na área útil da tela, então os diálogos não ficam mais cortados em 175% de escala. Abas, Error List, Output e Schema Compare seguem o tema escuro.

## Downloads

Desta [release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-beta.1):

| Arquivo | Finalidade |
| --- | --- |
| `PGMS-0.1.0-beta.1-Setup-x64.exe` | Instalador gráfico (inglês/pt-BR). Usuário atual sem direitos de administrador, ou todos os usuários com elevação. Não exige PowerShell nem runtime .NET separado. |
| `PGMS-0.1.0-beta.1-win-x64.zip` | Aplicativo portátil com runtime .NET/WPF. Extraia a pasta inteira e execute PGMS.exe. Inclui os scripts gerenciados do PowerShell 7. |
| `SHA256SUMS.txt` | SHA256 de cada arquivo desta release. Hashes conferem correspondência, não autoria. |
| `package-manifest.json` | Manifesto SHA256 de cada arquivo dentro do ZIP, conferido pelo `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | Estas notas. |

O instalador atualiza uma instalação existente no lugar; perfis, rascunhos, Query History e configurações ficam em `%LOCALAPPDATA%\PGMS` e não são removidos. A atualização sobre a alpha.7 numa máquina limpa ainda não foi certificada (veja [Validação](#validação)).

## Confira o que você baixou

Os binários não têm assinatura de código, então o SmartScreen do Windows pode avisar na primeira execução. Antes de escolher **Executar assim mesmo**, confira o checksum e a procedência:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-beta.1-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare os hashes com o `SHA256SUMS.txt`. O segundo comando precisa do [GitHub CLI](https://cli.github.com/) e confirma que o arquivo foi produzido pelo workflow de release deste repositório no commit da tag; rode também para o ZIP. A assinatura de código pelo programa de código aberto da SignPath Foundation está planejada, sem data prometida.

## Relatar um problema

Quando o PGMS mostrar a janela de erro inesperado, escolha **Report Issue** e anexe o log de **Open Log Folder** (`%LOCALAPPDATA%\PGMS\logs`). Para qualquer outro problema, [abra um relato de defeito](https://github.com/leoars/PostgresManagementStudio/issues/new?template=bug_report.yml): ele pede a versão do PGMS (Help ▸ About), as versões do Windows e do PostgreSQL, a forma de instalação, o idioma da interface, os passos e o log quando houver. Leia o log antes de anexar: uma mensagem de erro pode citar o que o servidor enviou. Problemas de tradução têm [formulário próprio](https://github.com/leoars/PostgresManagementStudio/issues/new?template=translation.yml).

## Escopo

Editor SQL com IntelliSense e sessões independentes, Object Explorer, Results paginado e Results to Text/File, exportação CSV/JSON, diagramas de banco e designers, planos gráficos, em texto e em JSON com comparação, administração PostgreSQL revisada, identidade/privilégios, Activity Monitor, estatísticas de consultas, Query History, Schema Compare, backup/restore lógico e o assistente de IA. PostgreSQL 16 a 18 e ferramentas cliente da mesma versão principal são necessários para operações no banco e backup/restore; servidor e ferramentas não acompanham o pacote. O SSMS não é necessário nem distribuído.

## Validação

Comportamento do aplicativo: a fonte M1145 passou na regressão completa deste repositório no PostgreSQL 17.11, e a fonte M1141 (as mesmas funções sem as seis mudanças de padrão do M1143 e as pastas de sistema do M1145) passou no 16.15, 17.11 e 18.6 (veja [validação](validation.md#v010-beta1--m1145--20112026)), num cluster isolado em loopback; o serviço PostgreSQL do host não é usado. Navegação só pelo teclado com teclas reais, Narrador, escala de 100/150/175% em três temas e três idiomas e os cenários de desempenho do projeto foram conferidos na máquina do mantenedor antes das mudanças de padrão. O PostGIS foi testado com o pacote oficial PostGIS 3.7.0 no PostgreSQL 17.11. As funções de IA foram testadas com um provedor falso local, não com um real.

Instalação numa VM Windows limpa e por outro usuário Windows sem privilégios não são certificadas por esta release.

## Limites conhecidos

Só Windows x64. Executável e instalador sem assinatura; o SmartScreen avisa. Sem MSI, winget ou atualização automática (só o aviso de nova versão). Não há alegação de paridade completa com o SSMS. Traduções sem revisão nativa. Um cursor só (sem múltiplos cursores). O Live Query Statistics não tem plano ao vivo, porque o PostgreSQL não tem contadores por operador enquanto a consulta roda; não há Analyze de plano. O OpenAI não foi testado com chave real. O Configure Server TLS precisa de servidor Windows e superusuário. Executar uma consulta de 10.000 × 20 com cinco abas abertas pode pausar a interface por cerca de 0,4 s, e reabrir cinco rascunhos recuperados leva cerca de 2 s. Com objetos de sistema visíveis, o `template0` aparece Offline e não pode ser expandido (ele não aceita conexões). Resultados: ordenação, cópia e exportação valem para a página atual; as linhas depois da primeira página ficam num arquivo temporário, sem criptografia, até a próxima execução ou até a aba fechar. O IntelliSense não conhece colunas de CTE, tabelas temporárias nem o `SET search_path` da sessão. Query Designer: `UNION` e `WITH` abrem só no painel SQL. O log de erros mascara senhas só na forma `chave=valor`.

## Versões anteriores

v0.1.0-alpha.7 (17/11/2026, conversa com a IA, tema escuro, log de erros), v0.1.0-alpha.6 (13/11/2026, índices e descrições no diagrama, Query History, Script as, avisos no plano) e v0.1.0-alpha.5 (08/11/2026, diagramas de banco, Query Designer, IntelliSense; Apache-2.0) continuam disponíveis com os arquivos e notas originais. v0.1.0-alpha.4 (07/11/2026), v0.1.0-alpha.3 (02/11/2026), v0.1.0-alpha.2 (01/11/2026, primeira release gerada pelo CI com atestado) e v0.1.0-alpha.1 (31/10/2026) continuam disponíveis com os arquivos e notas originais, sob MIT.
