# Como contribuir

[English](CONTRIBUTING.md) | Português (Brasil)

Use uma issue para descrever um defeito ou discutir mudanças substanciais antes de implementá-las. Inglês e português do Brasil são bem-vindos. Informe o build do PGMS, versões Windows/PostgreSQL, reprodução mínima com dados sintéticos, comportamento esperado e observado. Remova credenciais, dados de conexão e informações pessoais dos logs e capturas.

Leia o [guia de desenvolvimento](docs/pt-BR/development.md), a [arquitetura](docs/pt-BR/architecture.md) e [AGENTS.md](AGENTS.md). Trabalhe em uma branch, mantenha mudanças focadas e explique o que mudou, por quê e o que realmente testou. Preserve trabalho existente. Use o cluster isolado do projeto nos testes; nunca aponte o harness a um banco pessoal ou de produção.

Mudanças na documentação pública devem atualizar `docs/en` e `docs/pt-BR` e os dois READMEs quando pertinente. Não adicione histórico operacional interno, detalhes de máquinas pessoais ou artefatos brutos de aceitação ao repositório público.

Novos comportamentos, administração e mudanças de interface precisam de validação proporcional. Não declare paridade com SSMS ou certificação de plataformas somente por testes sintéticos. As verificações de desktop e banco exigem Windows e ferramentas PostgreSQL; informe o que não conseguiu executar.

O código e a documentação próprios do PGMS usam a [licença MIT](LICENSE); componentes de terceiros mantêm suas licenças. Envie somente material que possa contribuir nesses termos. Nunca inclua binários proprietários, ícones copiados de fornecedores ou segredos. Siga a [política de segurança](SECURITY.pt-BR.md) para vulnerabilidades.

Mensagens de commits criados pelo Codex devem começar com `[Codex]`, conforme preferência do autor do repositório. Esse prefixo não é exigido de contribuidores humanos.
