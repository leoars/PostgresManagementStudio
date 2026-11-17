# v0.1.0-alpha.7 — Windows x64

[English](../en/release-notes.md) | Português (Brasil)

Sétima pré-release pública do PGMS. É uma alpha para avaliação com dados sintéticos ou descartáveis. Rótulo de build: M1117. Os binários são gerados e atestados pelo mesmo [workflow de release](../../.github/workflows/release.yml) das versões anteriores. O trabalho próprio está sob Apache-2.0, com créditos no `NOTICE`; as versões até a alpha.4 continuam sob MIT.

## Destaques

- **Assistente de IA: conversa e Explain/Fix/Optimize.** A janela **Generate SQL with AI** agora aceita pedidos de seguimento: os cinco últimos pedidos da conversa e o SQL escrito para eles vão junto com o próximo, e **New Conversation** recomeça. **Query ▸ Explain SQL with AI**, **Fix SQL with AI** e **Optimize SQL with AI** abrem a janela com o SQL selecionado (ou o documento) e um pedido já escrito; o **Fix** acrescenta o SQLSTATE e a mensagem principal do último erro da aba, mas nunca o DETAIL, que pode citar valores de linhas. Nada é enviado antes de você clicar em **Generate**, e nada é executado: o SQL só vai para o editor. A estrutura do banco vai para o provedor que você configurar (Anthropic, OpenAI ou um Ollama local); dados das tabelas nunca são lidos nem enviados. Com o Ollama, o PGMS agora pede uma janela de contexto que caiba o prompt e corta a estrutura no máximo do modelo, em vez de deixar o servidor cortar o prompt em silêncio.
- **Configure Server TLS** não mudou: um assistente que instala um certificado e liga o TLS, restaurando as configurações anteriores se falhar. Exige conexão de superusuário a um servidor rodando no Windows.
- **Erros inesperados ficam registrados.** Um erro que o PGMS não tratou não fecha mais o programa em silêncio. Ele vai para um log diário em `%LOCALAPPDATA%\PGMS\logs` (guardado por 30 dias; valores de senha em texto `chave=valor` são mascarados) e uma janela mostra o erro com **Copy Details**, **Open Log Folder**, **Report Issue**, **Continue** (o padrão) e **Close PGMS**. Nada é enviado pela rede.
- **Tema escuro.** **Tools ▸ Theme** oferece Light, Dark e Use system setting. No tema escuro, a moldura, os menus, os controles padrão, as grades de resultados, a árvore e a janela Properties acompanham; diagramas e planos gráficos continuam claros, como papel. **Tools ▸ Editor Appearance** define as cores do editor separadamente (inclusive dois modos de contraste extra) e **View ▸ Presenter Mode** (`Ctrl+Shift+F5`) aumenta o texto do editor, dos resultados e da janela para apresentações.

## O que mudou desde a v0.1.0-alpha.6

**Editor**
- **Edit ▸ Advanced:** comentar e descomentar linhas e blocos, maiúsculas/minúsculas, mover, ordenar, juntar e duplicar linhas, apagar espaços horizontais, tabify/untabify, indentação, Insert File As Text, Expand Selection, Containing Block, Expand to Line, próxima/anterior subpalavra, Incremental Search, **Format SQL** (`Ctrl+K`, `Ctrl+Q`; só espaços, quebras de linha e caixa das palavras-chave mudam) e **Toggle Spell Checker** (comentários e cadeias, pelo verificador ortográfico do Windows).
- Uma barra de informações do editor com zoom (20–400 %, `Ctrl`+roda), linha e coluna (abre Go To Line), tabulações/espaços, fim de linha (CRLF/LF/CR) e codificação, com **Set End of Line Sequence**, **Set Indentation** e **Set Encoding** (codificações Unicode).
- **IntelliSense:** Parameter Info (`Ctrl+Shift+Space`) e Quick Info (`Ctrl+K`, `Ctrl+I`, ou passando o mouse).
- **Marcadores** por aba (`Ctrl+K`, `Ctrl+K` e os acordes do SSMS) e **Outlining** por instrução e comentário de bloco.
- **Modelos e trechos:** Template Explorer (`Ctrl+Alt+T`) com modelos PostgreSQL e seus próprios arquivos `.sql`, Specify Values for Template Parameters (`Ctrl+Shift+M`), Insert Snippet e Surround With.
- A posição do erro informada pelo servidor fica sublinhada no editor, contada na barra de informações e alcançada por **Go To Next/Previous Issue in File** (`Alt+PgDn`/`Alt+PgUp`).
- **Find in Files** e **Replace in Files** (`Ctrl+Shift+H`) numa pasta, nos documentos abertos ou no atual, com o painel Find Results.
- Histórico da área de transferência (`Ctrl+Shift+V`) e Go To Last Edit Location (`Ctrl+Shift+Backspace`).
- Linhas com mais de 10.000 caracteres aparecem cortadas, com um marcador que abre a linha inteira, e o editor não trava mais nelas.

**Arquivos, abas e janelas**
- **File ▸ New ▸ Query with Current Connection** e **Database Engine Query**; **Open ▸ File** conecta a aba nova com a conexão atual, e **File Disconnected…** abre sem conectar; **Disconnect All Queries**.
- Save All, Recent Files (quantidade em Tools ▸ Options), Close All Tabs e **Restore Closed Tab**, também para consultas sem arquivo.
- **Window:** próximo/anterior painel (`F6`), Show Results Pane (`Ctrl+R`), Split Tab, Dock, Auto Hide, Move To New Window, layouts de janela salvos (até dez) e **View ▸ Full Screen**.
- O menu agora divide a barra de título com os botões da janela e uma caixa **Search** (`Ctrl+Q`) que encontra comandos do menu e páginas e configurações de Tools ▸ Options.
- **View ▸ Properties Window** (`F4`), **Error List** (`Ctrl+\`, `E`) com o último erro de cada aba e **Output** (`Ctrl+Alt+O`) com as mensagens e execuções da sessão.
- **File ▸ Page Setup…** e **Print…** para o SQL da aba ativa.
- Textos de conexão mais próximos do SSMS: o nó do servidor mostra `host:porta (PostgreSQL versão - login)` e a barra de estado da consulta mostra servidor, login com o PID do backend, banco, tempo e linhas.

**Consultas e resultados**
- As abas Results/Messages ficam acima dos resultados, e **Save Results As…** no menu da grade abre **Save Grid Results** (`.csv` separado por vírgula ou `.txt` separado por tabulação, com cabeçalhos). A faixa de opções de exportação continua em **Export Options…**.
- **Query ▸ Include Live Query Statistics** mostra, uma vez por segundo e por uma conexão separada, o estado, o tempo decorrido, as esperas, as sessões que bloqueiam e o progresso de `pg_stat_progress_*` (CREATE INDEX, VACUUM, ANALYZE, CLUSTER, COPY) da consulta em execução. O PostgreSQL não tem contadores por operador, então não há plano ao vivo.
- **Query Shortcuts** (`Alt+F1`, `Ctrl+1` a `Ctrl+0`) em Tools ▸ Options, vazios por padrão.
- Tipos de extensões, como o `geometry` do PostGIS, aparecem como o texto que o servidor envia (EWKB em hexadecimal), sem erro.

**Object Explorer e administração**
- **Object Explorer Details** (`F7`), o conteúdo do nó selecionado com busca.
- **Keys**, **Constraints** e **Triggers**: Modify, Rename, Delete com o comando exato (sem CASCADE), New Foreign Key… e New Constraint…; Enable/Disable de triggers; New Function…, New Stored Procedure… e New Trigger… a partir de modelos.
- Uma pasta **Extensions** em cada banco com New Extension…, Update Extension… e Delete.
- Um submenu **Maintenance** com Vacuum…, Analyze… e Refresh Materialized View…; o comando abre numa aba de consulta, sem ser executado direto.
- Composite Types, Foreign Servers e Replication (publicações e assinaturas locais), só leitura.
- O filtro ganha **Does not equal**.
- **Role Memberships** lista as associações efetivas, diretas e herdadas, com o caminho.
- **Tools ▸ Schema Compare** compara dois bancos (só leitura) e escreve um script de mudanças numa aba de consulta conectada ao destino. Nunca executa o script.
- **Back Up:** compressão LZ4 e Zstandard. **Restore:** jobs paralelos (1–32) e **Choose Items…** para restaurar só alguns itens de um arquivo.
- **Connect:** certificado de cliente, chave de cliente e senha da chave (guardada com DPAPI); backup e restore recebem a senha da chave por um arquivo de serviço temporário e privado.

**Configurações**
- Tools ▸ Options: **Fonts and Colors** (Text Editor, Grid Results, Text Results), **Keyboard** (um atalho por comando do menu), opções de **Scripting** do Object Explorer, **Designers** e **Connection Dialog**.
- **Tools ▸ Import and Export Settings…** (um arquivo `.pgmssettings` com as opções), **External Tools…** e **Customize…** para as barras de ferramentas.
- **Help ▸ View Help** (`Ctrl+F1`) abre o guia do pacote; **Release Notes** abre a lista de releases.

**PostgreSQL 16:** quatro defeitos achados por uma regressão completa no PostgreSQL 16.15 foram corrigidos, entre eles o `MAINTAIN` oferecido em servidores anteriores ao 17.

## Downloads

Em [esta release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.7):

| Arquivo | Finalidade |
| --- | --- |
| `PGMS-0.1.0-alpha.7-Setup-x64.exe` | Instalador gráfico (inglês/pt-BR). Usuário atual sem administrador, ou todos os usuários com elevação. Não exige PowerShell nem runtime .NET separado. |
| `PGMS-0.1.0-alpha.7-win-x64.zip` | Aplicativo portátil com runtime .NET/WPF. Extraia a pasta inteira e execute PGMS.exe. Inclui os scripts gerenciados de PowerShell 7. |
| `SHA256SUMS.txt` | SHA256 de cada asset desta release. Hashes conferem correspondência, não autoria. |
| `package-manifest.json` | Manifesto SHA256 de cada arquivo dentro do ZIP, conferido por `Verify-Package.ps1`. |
| `RELEASE-NOTES.md`, `RELEASE-NOTES.pt-BR.md` | Estas notas. |

## Confira o que você baixou

Os binários não têm assinatura de código, então o Windows SmartScreen pode avisar na primeira execução. Antes de escolher **Executar assim mesmo**, confira o checksum e a procedência:

```powershell
Get-FileHash .\PGMS-* -Algorithm SHA256
gh attestation verify .\PGMS-0.1.0-alpha.7-Setup-x64.exe --repo leoars/PostgresManagementStudio
```

Compare os hashes com `SHA256SUMS.txt`. O segundo comando precisa do [GitHub CLI](https://cli.github.com/) e confirma que o arquivo foi produzido pelo workflow de release deste repositório no commit da tag; rode-o também para o ZIP. A assinatura de código pelo programa de código aberto da SignPath Foundation está planejada; não há data prometida.

## Relatar um problema

Quando o PGMS mostrar a janela de erro inesperado, escolha **Report Issue** e anexe o log de **Open Log Folder** (`%LOCALAPPDATA%\PGMS\logs`). Diga o que você estava fazendo, a versão do PostgreSQL e se o erro se repete. Leia o log antes de anexar: uma mensagem de erro pode citar o que o servidor enviou. Use os [modelos de issue](https://github.com/leoars/PostgresManagementStudio/issues/new/choose).

## Escopo

Editor SQL com IntelliSense e sessões independentes, Object Explorer, Results paginados e Results to Text/File, exportação CSV/JSON, diagramas de banco e designers, planos gráficos, administração PostgreSQL revisada, identity/privilégios, Activity Monitor, estatísticas de consultas, Schema Compare e backup/restore lógico. PostgreSQL 16 a 18 e ferramentas cliente da mesma versão principal são necessários para operações de banco e backup/restore; servidor e ferramentas não acompanham. O SSMS não é necessário nem distribuído.

## Validação

Comportamento do aplicativo: a fonte M1117 passou na regressão completa deste repositório no PostgreSQL 17.11 (ver [validação](validation.md#v010-alpha7--m1117--17112026)) num cluster isolado em loopback; o serviço PostgreSQL do host não é usado. A maior parte das funções acrescentadas desde a alpha.6 foi validada por recortes automatizados durante a construção; a última regressão completa no PostgreSQL 16.15 e 18.6 foi sobre uma fonte anterior (M1066). As ações novas de IA e a conversa foram testadas só com um provedor falso local, não com um real. O PostGIS não foi instalado no cluster de testes: foi usado um tipo `geometry` substituto.

Instalação numa VM Windows limpa, outro usuário Windows sem privilégios e observações físicas de DPI, teclado e leitor de tela das telas novas não são certificadas por esta release.

## Limites conhecidos

Somente Windows x64. Executável e instalador sem assinatura; o SmartScreen avisa. Sem MSI, atualização automática ou garantia de suporte em produção. Não há alegação de paridade integral com o SSMS. Tema escuro: diagramas e planos continuam claros; não há temas coloridos. Um cursor só (sem cursores múltiplos). Vários conjuntos de resultados aparecem em abas, não empilhados. Live Query Statistics não tem plano ao vivo. Valores do PostGIS aparecem como texto hexadecimal, sem WKT, GeoJSON ou mapa. Resultados: ordenação, cópia e exportação valem para a página atual; as linhas depois da primeira página ficam num arquivo temporário, sem criptografia, até a próxima execução ou até fechar a aba; Results to Text mantém os limites de 10.000 linhas e 250.000 células, e Results to File não tem nenhum dos dois. O IntelliSense não conhece colunas de CTE, tabelas temporárias nem o `SET search_path` da sessão. Query Designer: `UNION` e `WITH` abrem só no painel SQL. Find Missing Foreign Keys é uma heurística: um nome parecido é pista, não prova. O log de erros mascara senhas só no formato `chave=valor`.

## Versões anteriores

v0.1.0-alpha.6 (13/11/2026, índices e descrições nos diagramas, Query History, Script as, avisos no plano) e v0.1.0-alpha.5 (08/11/2026, diagramas de banco, Query Designer, IntelliSense; Apache-2.0) continuam disponíveis com seus assets e notas originais. v0.1.0-alpha.4 (07/11/2026, Activity Monitor em aba), v0.1.0-alpha.3 (02/11/2026, Query Statistics), v0.1.0-alpha.2 (01/11/2026, primeira release do CI com atestado) e v0.1.0-alpha.1 (31/10/2026) continuam disponíveis com seus assets e notas originais, sob MIT.
