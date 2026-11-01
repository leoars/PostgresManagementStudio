# PGMS v0.1.0-alpha.2 — distribuição portátil (M1008)

[English]: READ-ME.md no pacote, ou docs/en/distribution.md no repositório. Português (Brasil).

Extraia todo o diretório e execute PGMS.exe. Pacote win-x64 self-contained inclui .NET/WPF; preserve DLLs, recursos, LICENSE, avisos e pasta licenses juntos. SSMS não é necessário. Não inclui servidor ou ferramentas PostgreSQL. Use PostgreSQL 17/18 e pg_dump/pg_restore/psql/pg_dumpall da mesma versão principal conforme a operação. Sem instalador assinado, MSI ou atualização automática.

Conecte com a role pretendida e TLS adequado (VerifyFull por padrão). Revise SQL, destino e role antes de administrar. Execute restore somente com scripts confiáveis; SQL pode emitir COMMIT, reconectar ou executar comandos locais psql. Cancelar é solicitação e pode chegar após COMMIT. Confira o destino antes de repetir.

Cada aba tem sessão própria. F5 executa seleção/documento. Ctrl+C copia células selecionadas exibidas com cabeçalhos; Ctrl+Shift+C omite cabeçalhos. NULL difere de vazio; resultados/cópia/exportação têm limites explícitos. Exportação não é dump completo do banco.

Estado padrão em %LOCALAPPDATA%/PGMS; PGMS_STATE_DIRECTORY permite pasta separada de avaliação. Credenciais salvas, recuperação e diários usam DPAPI do usuário Windows atual. Feche antes de copiar estado; preserve SQL separadamente e revise abas recuperadas com Save As. Estado/credenciais não são universalmente portáteis entre usuários/máquinas ou builds antigos.

Confira integridade com ./Verify-Package.ps1 -PackagePath . no PowerShell 7; hashes detectam diferenças, não autoria. Instalação gerenciada opcional também exige PowerShell 7. No pacote extraído: ./Install-Pgms.ps1 -PackagePath .; consulte ./Get-PgmsInstallation.ps1; abra ./Start-Pgms.ps1; use ./Set-PgmsActiveVersion.ps1 -Milestone <rótulo-instalado> no rollback e ./Uninstall-Pgms.ps1 -Milestone <rótulo-instalado> na remoção. Use -InstallRoot para local gerenciado isolado. Scripts preservam estado do usuário e recusam raiz não gerenciada/não vazia e caminhos inseguros; feche instâncias primeiro.

Fonte, guias bilíngues e relatos: https://github.com/leoars/PostgresManagementStudio . Trabalho próprio MIT; licenças de terceiros e condições de marca são separadas. PGMS é independente, sem paridade integral SSMS ou endosso.

Os assets da release são compilados pelo GitHub Actions a partir de uma tag de versão e publicados como rascunho revisado pelo mantenedor. A página da release traz um único SHA256SUMS.txt cobrindo todos os assets e um atestado de proveniência de build, verificável com o GitHub CLI: gh attestation verify <asset> --repo leoars/PostgresManagementStudio. Um instalador gráfico EXE (inglês/pt-BR, por usuário ou para todos) é publicado junto com este ZIP; o instalador PowerShell de download da v0.1.0-alpha.1 não faz parte desta release.
