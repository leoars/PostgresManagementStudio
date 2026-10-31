# v0.1.0-alpha.1 — Windows x64

[English](../en/release-notes.md) | Português (Brasil)

Primeira pré-release binária pública do PGMS. É uma versão alpha para avaliação com dados sintéticos/descartáveis. Rótulo de build: M1008. Trabalho próprio MIT; avisos e licenças das dependências acompanham o pacote.

## Downloads

Nesta [release](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.1):

| Arquivo | Finalidade |
| --- | --- |
| `PGMS-0.1.0-alpha.1-win-x64.zip` | Aplicativo portátil com runtime .NET/WPF. Extraia toda a pasta e execute PGMS.exe. |
| `Install-PGMS-0.1.0-alpha.1.ps1` | Instalador opcional PowerShell 7: baixa este ZIP exato, confere SHA256 fixado e instala para o usuário atual. |
| `SHA256SUMS.txt` | Valores SHA256 do ZIP e instalador. Hash confere correspondência, não é assinatura digital. |

O aplicativo portátil não exige PowerShell 7 ou runtime .NET global. Instalador e scripts gerenciados exigem PowerShell 7 e não alteram a política de execução do sistema. Não há instalador gráfico EXE/MSI. Revise scripts sem assinatura conforme a política da sua organização.

```powershell
Get-FileHash ./PGMS-0.1.0-alpha.1-win-x64.zip -Algorithm SHA256
pwsh -File ./Install-PGMS-0.1.0-alpha.1.ps1
```

O instalador usa `%LOCALAPPDATA%/Programs/PGMS`, preserva versões gerenciadas anteriores e recusa destino não gerenciado não vazio. `-InstallRoot` escolhe outro local gerenciado; `-ArchivePath` usa ZIP já baixado com o mesmo hash obrigatório. Não abre PGMS nem conecta ao banco. Abra o caminho do executável informado pelo instalador, ou use Start-Pgms.ps1 da pasta da versão instalada. Scripts de rollback/remoção estão incluídos; perfis/estado local são preservados. Veja o [guia de distribuição](distribution.md).

## Instalador gráfico adicionado após a release original

`PGMS-0.1.0-alpha.1-Setup-x64.exe` e seu `.sha256` separado adicionam assistente nativo em inglês/pt-BR, escolha por usuário/para todos, atalho padrão na área de trabalho e desinstalação nativa. Arquivos do aplicativo, ZIP/assets originais e tag permanecem iguais. Este instalador passou em 17 checks por usuário e revisão visual; execução elevada para todos ainda não foi testada. Veja o [guia do instalador gráfico](graphical-installer.md) para requisitos, fonte, compilação e limites. O instalador PowerShell descrito acima continua disponível.

## Escopo

Editor SQL e sessões independentes, Object Explorer, Results/Messages com limites e exportação CSV/JSON, administração PostgreSQL revisada, identity/privilégios, monitor e backup/restore lógico. PostgreSQL 17/18 e ferramentas da mesma versão principal são necessários para operações com banco e backup/restore; não são incluídos. SSMS não é exigido nem distribuído.

## Validação

Regressão completa PostgreSQL 17.11 aprovada: **2.115 integração +3.072 WPF =5.187 checks**, com cluster isolado em loopback na porta 55441, encerrado ao final. Onze checks do parser de porta e duas recusas de porta inválida no runner passaram separadamente. Uma corrida no sinal de prontidão de um teste, encontrada na primeira tentativa, foi corrigida com publicação atômica; recortes passaram 26+20 e depois toda a suíte acima passou. Não foi necessário alterar firewall ou encerrar processos alheios.

O pacote Release final passou em manifesto, licença MIT/terceiros, guias bilíngues, extração ZIP e abertura/saída normal com runtime .NET/WPF 10.0.14 incluído. Símbolos de debug foram excluídos e a varredura não encontrou caminhos locais de build ou arquivos de estado do usuário no pacote.

O instalador fixado passou em **12 checks**, incluindo recusa de hash alterado, proteção de destino não gerenciado, recusa de duplicação, atualização/rollback/reativação instalados e remoção completa. Quatro aberturas instaladas usaram runtime próprio e saíram 0; dados sintéticos externos foram preservados. Os testes ocorreram no host de desenvolvimento, com estado isolado, sem nova certificação em VM Windows limpa.

Revise a [fonte do instalador](../../scripts/Install-PgmsRelease.ps1) antes de executar.

## Limites conhecidos

Somente Windows x64. Executável/scripts sem assinatura; SmartScreen não testado. Sem MSI, atualização automática ou garantia de suporte em produção. Não se declara paridade integral SSMS. Resultados/recuperação têm limites; linhas SQL grandes podem pausar renderização. Cancelar não comprova rollback. SQL de restore exige confiança e pode controlar transações, reconectar ou executar comandos locais psql. Estado DPAPI depende do usuário Windows. Observações físicas/Windows limpo históricas mantêm seu escopo; não se certifica nova instalação desta release em VM Windows limpa. Veja [validação](validation.md) e [segurança](../../SECURITY.pt-BR.md).
