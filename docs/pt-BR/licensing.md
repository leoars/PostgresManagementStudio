# Licenças e atribuição

[English](../en/licensing.md) | Português (Brasil)

## Trabalho próprio

Código e documentação próprios do PGMS usam a [licença MIT](../../LICENSE), escolhida pelo mantenedor. Preserve o aviso de copyright e permissão nas cópias ou partes substanciais. O texto canônico está em inglês; este guia explica a organização do projeto, sem substituir o texto. Software, recursos e marcas de terceiros não se tornam propriedade MIT do PGMS por essa escolha.

## Dependências e recursos

As versões seguem os `packages.lock.json` atuais. Textos originais estão em [docs/licenses](../licenses); os [avisos originais](../../THIRD-PARTY-NOTICES.md) foram preservados em português.

| Componente | Versão | Licença / origem |
| --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT; [projeto](https://github.com/icsharpcode/AvalonEdit) |
| AvalonDock/Core/serializadorXML/temasVS | 5.0.1 | Ms-PL; [projeto](https://github.com/Dirkster99/AvalonDock) |
| Npgsql | 10.0.3 | PostgreSQL License; [projeto](https://github.com/npgsql/npgsql) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT; [runtime](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT; [runtime](https://github.com/dotnet/runtime) |
| Runtime .NET/WPF self-contained | depende do pacote | Licenças/avisos copiados dos runtime packs ao empacotar; M1008 inclui 10.0.14. |
| Elefante PostgreSQL/Slonik | arte original | Licença em [PostgreSQL-logo.txt](../licenses/PostgreSQL-logo.txt); [press kit](https://www.postgresql.org/about/press/presskit12/) e [política de marcas](https://www.postgresql.org/about/policies/trademarks/). |

A arte do aplicativo inclui o elefante PostgreSQL. Permissão autoral e condições de marca são distintas. A licença MIT do PGMS não concede permissão para alterar/combinar marcas ou sugerir endosso. Preserve a origem da arte; qualquer redesign público da marca deve seguir a política PostgreSQL. PGMS é independente, sem endosso ou afiliação com Microsoft ou projeto PostgreSQL.

## Mecanismo do instalador

O instalador gráfico opcional usa [Inno Setup 7.1.1](https://jrsoftware.org/isinfo.php). O mecanismo preserva créditos do fornecedor e sua [licença](https://github.com/jrsoftware/issrc/blob/main/license.txt) separada, instalada também como `INNO-SETUP-LICENSE.txt`. A licença MIT do PGMS não substitui esses termos. O compilador é ferramenta de build; o aplicativo não depende dele instalado globalmente para executar.

## Materiais da referência

SSMS é referência de interface, não implementação redistribuída. O inventário versionado contém fonte/recursos PGMS, sem binários SSMS. A captura pública é do PGMS com dados sintéticos e [origem documentada](../assets/README.md); não estabelece licença para capturas de fornecedores. Não adicione assemblies, ícones proprietários ou fonte copiada de fornecedores.

Builds da fonte copiam LICENSE, avisos de terceiros e os dois idiomas do guia de distribuição ao pacote. A primeira pré-release binária é v0.1.0-alpha.1; veja sua validação nas notas da versão.
