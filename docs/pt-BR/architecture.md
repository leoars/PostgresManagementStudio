# Arquitetura

[English](../en/architecture.md) | Português (Brasil)

Esta página descreve a estrutura implementada, não todos os módulos propostos nos documentos iniciais.

| Projeto/diretório | Responsabilidade |
| --- | --- |
| `src/PGMS.Desktop` | Shell WPF, docking, AvalonEdit, grades, diálogos, perfis de conexão, arquivos e recuperação local. |
| `src/PGMS.PostgreSql` | Sessões Npgsql, catálogo, execução, edições administrativas tipadas e ferramentas externas PostgreSQL. |
| `tests/PGMS.IntegrationTests` | Comportamento real no servidor com bancos sintéticos descartáveis no cluster do projeto. |
| `tests/PGMS.DesktopTests` | Contratos WPF, arquivos/recuperação, UI Automation e provas de processos filhos instalados. |
| `scripts` | Build, orquestração de testes isolados PostgreSQL, validação de pacote/instalação e evidências. |
| `tools/PGMS.TraceWindows` | Ferramenta de profiling de desenvolvimento; não é dependência distribuída. |

## Limites

O desktop usa os serviços PostgreSQL por referência de projeto. Cada aba possui sua sessão, incluindo transação e configurações; inspeções e operações administrativas podem usar conexões próprias. Roles do servidor continuam sendo a autoridade. Operações tipadas geram SQL PostgreSQL e aplicam guardas de identidade/concorrência específicas; não são tradutor de DDL arbitrário.

Controles WPF e renderização pertencem à thread de UI. I/O e várias etapas de preparação/checkpoint usam trabalho assíncrono, mas linhas SQL grandes ainda podem pausar a interface. Resultados e recuperação têm limites explícitos de retenção. Cancel é solicitação, não prova de rollback ou encerramento garantido em prazo.

Perfis, layout, rascunhos e diários administrativos ficam em diretório local configurável. Segredos e payloads de recuperação usam DPAPI do usuário Windows atual. Formatos são versionados; binários antigos não têm compatibilidade retroativa universal. Ferramentas PostgreSQL externas executam como processos filhos, com argumentos revisados e staging temporário protegido quando aplicável.

Observações SSMS e provas REA orientam somente desenvolvimento. Nenhum assembly SSMS ou componente REA é exigido pelo aplicativo distribuído. Veja [licenças](licensing.md), [validação](validation.md) para comportamento suportado e limites.
