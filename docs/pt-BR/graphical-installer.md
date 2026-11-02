# Instalador gráfico

[English](../en/graphical-installer.md) | Português (Brasil)

Baixe `PGMS-0.1.0-alpha.3-Setup-x64.exe` na [release alpha.3](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.3) e confira contra o `SHA256SUMS.txt` (e, opcionalmente, com `gh attestation verify`). Desde a v0.1.0-alpha.3 o instalador é compilado pelo workflow de release a partir do mesmo payload do ZIP portátil daquela release; na v0.1.0-alpha.1 ele foi adicionado depois, embrulhando o payload M1022 existente. O instalador dispensa PowerShell e runtime .NET instalado separadamente.

## Opções de instalação

Escolha inglês ou português do Brasil e depois o modo de instalação:

- **Só para mim:** dispensa administrador; padrão `%LOCALAPPDATA%\Programs\PostgresManagementStudio`.
- **Para todos os usuários:** o Windows solicita privilégios administrativos; padrão `%ProgramFiles%\PostgresManagementStudio`. Arquivos do programa e atalhos ficam disponíveis para todos.

O atalho na área de trabalho vem selecionado e pode ser desmarcado. São criados atalho no menu Iniciar e entrada em Aplicativos Instalados/desinstalador. Abrir o PGMS ao finalizar é opcional e vem desmarcado. Este instalador é destinado a Windows x64, Windows 10 build 19045 ou posterior; outras arquiteturas não foram certificadas.

Conexões, credenciais salvas, layout e recuperação permanecem em `%LOCALAPPDATA%\PGMS` de cada usuário, com DPAPI por usuário. Instalação global não compartilha credenciais nem instala servidor/ferramentas PostgreSQL. Feche o PGMS antes de instalar ou reparar. Não há atualizador automático nem migração da instalação PowerShell. A instalação PowerShell usa outra raiz (`Programs\PGMS`); remova uma instalação antiga pelo próprio método de remoção após conferir qual atalho utiliza.

## Reparo e remoção

Execute novamente o instalador com o mesmo modo e diretório para reparar esta versão. Diretórios desconhecidos não vazios e pastas vinculadas são recusados. Um executável PGMS bloqueado impede o reparo, sem encerrar o aplicativo à força. A desinstalação nativa remove os próprios arquivos e atalhos; perfis, recuperação e arquivos SQL adicionais são preservados. Mantenha arquivos SQL importantes fora do diretório de instalação e faça backups separados.

O executável não tem assinatura digital; reputação SmartScreen e VM Windows limpa não foram certificadas. Instalação por usuário, hashes exatos do pacote, abertura com runtime incluído, reparo em português, remoção nativa, atalho padrão, recusa de destinos não gerenciados/vinculados/bloqueados e preservação de arquivos sintéticos passaram em 17 checks. Páginas em português até Pronto para Instalar e boas-vindas/cancelamento em inglês foram revisados visualmente. **Elevação e execução para todos os usuários ainda não foram testadas**; atualização para versão posterior também não.

## Compilar e testar

Use o [compilador oficial Inno Setup 7.1.1 x64](https://jrsoftware.org/isdl.php), preservando sua licença. Extraia o ZIP existente da release em `artifacts/M1022-win-x64`. O builder confere cada arquivo, o manifesto congelado e a versão do aplicativo, recusando diretório de saída existente. Uma recompilação local pode produzir outro manifesto e é intencionalmente recusada por este builder específico da release. Atualize a configuração para uma futura release congelada. O [workflow de release](../../.github/workflows/release.yml) passa `-SkipFrozenManifest` porque compila o próprio payload; marco e versão do produto continuam exigidos, e o hash congelado segue obrigatório em builds locais.

```powershell
./scripts/Build-GraphicalInstaller.ps1 -PackagePath artifacts/M1022-win-x64 -CompilerPath 'C:\Tools\Inno Setup 7\ISCC.exe'
./scripts/Test-GraphicalInstaller.ps1 -InstallerPath artifacts/graphical-installer/0.1.0-alpha.1/PGMS-0.1.0-alpha.1-Setup-x64.exe
```

O teste instala e desinstala de verdade por usuário, usando diretório e grupo no menu Iniciar únicos. Recusa execução se este produto já estiver registrado ou seu atalho de área de trabalho existir. Execute em conta Windows isolada; o teste preserva evidências em `artifacts/graphical-tests`, ignorado pelo Git. Não eleva privilégios nem usa banco de dados. O compilador não vira dependência de execução do aplicativo. O instalador preserva os créditos do Inno Setup e instala `INNO-SETUP-LICENSE.txt` junto do desinstalador nativo.
