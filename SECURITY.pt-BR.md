# Segurança

[English](SECURITY.md) | Português (Brasil)

O PGMS está em desenvolvimento. Não há série de releases publicada com suporte nem prazo de resposta garantido. O desenvolvimento atual e o candidato M1002 são o ponto de partida para investigação; pacotes históricos não recebem correções automaticamente.

Não divulgue vulnerabilidade sem correção ou credenciais reais em uma issue pública. Use **Report a vulnerability** no GitHub deste repositório se o relato privado estiver habilitado. Se estiver indisponível, abra uma issue pedindo ao mantenedor um canal privado, sem detalhes da vulnerabilidade. Nunca publique dumps, perfis salvos, payloads DPAPI, tokens ou chaves privadas.

O relato privado deve informar builds afetados, reprodução mínima sintética, impacto, comportamento esperado e mitigação proposta, se conhecida. O PGMS executa administração intencionalmente; explique como o problema ultrapassa uma barreira prevista de permissão, revisão ou dados.

Perfis e recuperação usam DPAPI do usuário Windows. Isso não garante portabilidade entre contas/máquinas, proteção contra administradores ou execução segura de SQL não confiável. O aplicativo usa as permissões da role PostgreSQL conectada. Revise SQL e destinos antes de administrar ou restaurar. Veja o [guia de uso](docs/pt-BR/user-guide.md).
