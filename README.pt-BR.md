# PGMS — Postgres Management Studio

[English](README.md) | Português (Brasil)

Aplicativo Windows para consultar e administrar PostgreSQL, desenvolvido em C#, WPF, .NET 10 e Npgsql. A interface tem o SQL Server Management Studio (SSMS) como referência, com semântica PostgreSQL e implementação própria.

**Pré-release: v0.1.0-alpha.1 (Windows x64).** [Download e instalação](https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.1) · [Notas da versão](docs/pt-BR/release-notes.md). ZIP portátil inclui .NET/WPF; o instalador opcional é um script PowerShell 7. O rótulo M1008 é interno. Veja o [guia de validação](docs/pt-BR/validation.md) para escopo aceito e limites conhecidos; não se declara paridade integral com SSMS.

![Editor, Object Explorer e resultados sintéticos do PGMS](docs/assets/pgms-results-m959.jpg)

Prévia histórica M957 capturada na M959, com cluster isolado e dados sintéticos. A imagem ilustra a interface, não o pacote atual. [Origem da imagem](docs/assets/README.md).

## Funcionalidades

- Editor SQL: destaque, busca/substituição, múltiplas abas, abrir/salvar e recuperação criptografada de rascunhos.
- Sessões PostgreSQL independentes, executar/cancelar, Results/Messages, grade com limites, cópia e exportação CSV/JSON.
- Object Explorer, inspeção, planos estimados/analisados e configuração da sessão.
- Administração com SQL revisável nos casos suportados de bancos, schemas, tabelas, colunas, identity, índices, constraints, sequences, roles e privilégios.
- Monitor de sessões/bloqueadores, com cancelamento de consulta e encerramento de sessão distintos.
- Backup/restore lógico por ferramentas externas PostgreSQL: custom, tar, SQL e diretório, além de operações globais revisadas.

## Começar

Para desenvolvimento: Windows x64, PowerShell 7 e SDK .NET selecionado por [global.json](global.json) (`10.0.400`, com o patch mais recente instalado nessa faixa). PostgreSQL 17/18 e ferramentas da mesma versão principal são necessários para testes com banco e backup/restore.

```powershell
git clone https://github.com/leoars/PostgresManagementStudio.git
cd PostgresManagementStudio
./scripts/Build.ps1
dotnet run --project src/PGMS.Desktop
```

O pacote portátil self-contained inclui .NET/WPF, sem servidor ou ferramentas PostgreSQL. ZIPs candidatos e artefatos locais de aceitação não vêm em um clone novo. Veja [preparação e empacotamento](docs/pt-BR/getting-started.md).

## Documentação

| Assunto | English | Português (Brasil) |
| --- | --- | --- |
| Preparação e empacotamento | [Guide](docs/en/getting-started.md) | [Guia](docs/pt-BR/getting-started.md) |
| Consultas, administração, backup | [User guide](docs/en/user-guide.md) | [Guia de uso](docs/pt-BR/user-guide.md) |
| Build, testes, contribuições | [Development](docs/en/development.md) | [Desenvolvimento](docs/pt-BR/development.md) |
| Estrutura | [Architecture](docs/en/architecture.md) | [Arquitetura](docs/pt-BR/architecture.md) |
| Provas e limites | [Validation](docs/en/validation.md) | [Validação](docs/pt-BR/validation.md) |
| Atribuição | [Licensing](docs/en/licensing.md) | [Licenças](docs/pt-BR/licensing.md) |

[Índice da documentação](docs/README.md). Guias públicos são mantidos nos dois idiomas; o histórico interno original não está incluído nesta cópia pública selecionada. Documentação bilíngue não localiza a interface do aplicativo.

## Contribuir e relatar problemas

Veja [CONTRIBUTING.pt-BR.md](CONTRIBUTING.pt-BR.md). Inglês e português do Brasil são bem-vindos. Use dados sintéticos e remova credenciais e informações pessoais dos relatos. Para vulnerabilidades, siga [SECURITY.pt-BR.md](SECURITY.pt-BR.md).

## Licença

Código e documentação próprios do PGMS usam a [MIT](LICENSE). Componentes de terceiros e o elefante PostgreSQL mantêm suas licenças e condições de marca; veja os [avisos de terceiros](THIRD-PARTY-NOTICES.md). O PGMS é independente, sem endosso da Microsoft ou do projeto PostgreSQL. SSMS é referência, sem dependência distribuída.
