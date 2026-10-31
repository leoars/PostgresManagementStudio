# Public source policy

English | [Português (Brasil)](../pt-BR/public-readiness.md)

This repository is a curated source import with a new Git history. It includes original application code, synthetic test fixtures, selected reproducible build/package scripts, MIT licensing, dependency notices and bilingual public guides. Private development conversations, operational memory/backlog, historical milestone archives, workstation/laboratory setup notes and raw acceptance artifacts are not published here.

Use GitHub's noreply address for commits when you do not want to publish a personal email. Keep screenshots limited to synthetic data. Do not add saved connection profiles, database dumps, tokens, private keys, encrypted recovery payloads, machine-specific configuration or build/test artifacts. The `.gitignore` is a convenience, not a substitute for reviewing staged content.

The source import is reviewed with Gitleaks without historical allowlist exceptions. Pattern-based scanning does not prove that every possible secret is absent. Review future changes before publishing them. Source availability does not publish or certify a binary release. See [validation](validation.md), [licensing](licensing.md) and the [security policy](../../SECURITY.md).
