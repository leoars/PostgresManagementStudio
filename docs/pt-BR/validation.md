# Validação e limites

[English](../en/validation.md) | Português (Brasil)

O PGMS está em desenvolvimento. M1002 é o rótulo interno do build importado neste repositório, não release pública nem garantia de identidade binária de uma recompilação local. Histórico operacional e provas brutas privados não são incluídos intencionalmente.

A implementação importada passou anteriormente em 5.187 checks automatizados (2.115 integração PostgreSQL e 3.072 WPF), além de abertura de pacote, preservação de perfil instalado e recuperação de três rascunhos entre rollback/reinício. São observações históricas, não testes novos a cada revisão. Reproduza o comportamento atual pelos scripts públicos e informe ambiente/resultados reais. As verificações da revisão pública inicial são registradas abaixo após execução.

Limites conhecidos: Windows x64; PostgreSQL 17/18 com ferramentas da mesma versão principal, cobertura histórica17.11/18.6;16 e anteriores não suportados. Sem paridade integral SSMS. Linhas SQL enormes podem pausar renderização. Resultados/recuperação têm limites. Cancelar não comprova rollback. DPAPI depende do usuário. Pacote sem assinatura/MSI/atualização automática; SmartScreen não certificado. DPI100/125/150/175% exercitados internamente;200% aceito sem execução. Reaparecimento automático após desconexão, DPAPI entre usuários e nova prova desta importação em Windows limpo não certificados. Observações de clipboard/teclado/leitor são históricas, não reproduzidas por checks WPF comuns.

```powershell
./scripts/Build.ps1 -Configuration Release
./scripts/Test-Integration.ps1
./scripts/Test-Package.ps1
./scripts/Test-Installation.ps1
./scripts/Test-PublicDocumentation.ps1
```

O harness cria cluster descartável em loopback no diretório artifacts ignorado deste checkout, exclusivamente 127.0.0.1:55439, e o encerra ao final. Nunca use bancos pessoais ou de produção nesses testes. Veja [desenvolvimento](development.md).

## Checks da fonte pública inicial — 31/10/2026

Build Release passou sem avisos/erros. Foram 157 checks focados de arquivos desktop, 22 de pacote e 46 de instalação em fixtures. A documentação foi conferida em 12 pares de idiomas e links locais. Pacote self-contained local passou em manifesto/licença/guias bilíngues, carregou runtime próprio e fechou normalmente com saída0/estado isolado. Nenhuma release binária foi enviada.

A regressão PostgreSQL completa foi tentada, mas parou antes de iniciar o servidor porque Windows negou bind na porta isolada 55439 (erro de acesso ao socket). Uma prova separada em loopback reproduziu; porta dinâmica funcionou. Não se atribui repetição dos 5.187 checks históricos a esta importação. O cluster descartável não estava ativo ao final. Não foram alterados firewall ou bancos do usuário. Gitleaks não encontrou alertas na fonte selecionada, sem exceções históricas.
