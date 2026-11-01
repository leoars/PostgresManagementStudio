# Security

English | [Português (Brasil)](SECURITY.pt-BR.md)

PGMS is in development. There is no published supported release series or guaranteed response time. Current development and the v0.1.0-alpha.2 pre-release are the starting points for investigation; historical packages do not automatically receive fixes.

Do not disclose an unpatched vulnerability or real credentials in a public issue. Use GitHub's **Report a vulnerability** on this repository if private reporting is enabled. If it is unavailable, open an issue asking the maintainer for a private contact channel without including vulnerability details. Never post database dumps, saved profiles, DPAPI payloads, access tokens or private keys.

A private report should contain affected builds, a minimal synthetic reproduction, impact, expected behavior and proposed mitigation if known. PGMS intentionally performs administrative actions; explain how the issue crosses an intended permission, review or data boundary.

Profiles and recovery use Windows user-scoped DPAPI. This does not guarantee portability across accounts or machines, protection from administrators or safe execution of untrusted SQL. The application uses the permissions of the connected PostgreSQL role. Review SQL and destinations before applying administration or restore. See the [user guide](docs/en/user-guide.md).
