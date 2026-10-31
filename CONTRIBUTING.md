# Contributing

English | [Português (Brasil)](CONTRIBUTING.pt-BR.md)

Use an issue to describe a bug or discuss a substantial change before implementing it. English and Brazilian Portuguese are welcome. Include the PGMS build, Windows/PostgreSQL versions, a minimal reproduction with synthetic data, expected behavior and actual behavior. Remove credentials, connection details and personal information from logs and screenshots.

Read the [development guide](docs/en/development.md), [architecture](docs/en/architecture.md) and repository [AGENTS.md](AGENTS.md). Work on a branch, keep changes focused and explain what changed, why and what you actually tested. Preserve existing work. Use the isolated project cluster for tests; never point the test harness at a personal or production database.

Changes to public documentation must update both `docs/en` and `docs/pt-BR` (and both READMEs when relevant). Do not add internal operational histories, personal machine details or raw acceptance artifacts to this public source repository.

New behavior, administration or UI changes should include proportional validation. Do not claim SSMS parity or certify platforms based only on synthetic tests. Desktop and database checks require Windows and PostgreSQL tools; state any checks you could not run.

PGMS's original code and documentation use the [MIT license](LICENSE); third-party components retain their own licenses. Submit only material you may contribute under these terms. Never include proprietary binaries, copied vendor icons or secrets. Follow the [security reporting policy](SECURITY.md) for vulnerabilities.

Codex-created commit messages must start with `[Codex]`, as required by the repository's author. This prefix is not required of human contributors.
