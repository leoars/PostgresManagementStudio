# Third-party notices / Dependências de terceiros

English first; the original Portuguese notice follows. / Inglês primeiro; o aviso original em português vem em seguida.

## English

PGMS uses the following packages, with resolved versions recorded in the `packages.lock.json` files:

| Component | Version | License / source |
| --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT — https://github.com/icsharpcode/AvalonEdit |
| AvalonDock, Core, Serializer.Xml, Themes.VS | 5.0.1 | Microsoft Public License (Ms-PL), per the LICENSE of the packages used — https://github.com/Dirkster99/AvalonDock |
| Npgsql | 10.0.3 | PostgreSQL License — https://github.com/npgsql/npgsql |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT — https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT — https://github.com/dotnet/runtime |

License texts are in `docs/licenses`. The simple shell icons were drawn in the project's own XAML. No assemblies or icons were copied from the SSMS installation. The reference product name and library names identify their respective products and authors.

Self-contained packages include Microsoft.NETCore.App and Microsoft.WindowsDesktop.App, in the versions recorded in PGMS.runtimeconfig.json. Publish copies the available licenses and notices from the respective runtime packs into `licenses`. The runtime is not an SSMS component.

The optional graphical installer is built with Inno Setup; its engine keeps the vendor credits and its separate license, also installed as `INNO-SETUP-LICENSE.txt`. See [docs/en/licensing.md](docs/en/licensing.md).

### PostgreSQL elephant (Slonik)

The icon and logo use the official three-color vector available at https://wiki.postgresql.org/images/a/a4/PostgreSQL_logo.3colors.svg, listed at https://wiki.postgresql.org/wiki/Logo. Geometry preserved; inherited styles were made explicit and the symbol was resized. The PGMS composition adds the application name and tagline without overlapping or distorting the elephant.

The press kit https://www.postgresql.org/about/press/presskit12/ provides the logos under the PostgreSQL License. The text is preserved in docs/licenses/PostgreSQL-logo.txt. The PostgreSQL name and Slonik are trademarks of the PostgreSQL Community Association of Canada, subject to https://www.postgresql.org/about/policies/trademarks/. PGMS is an independent project and does not claim affiliation with or endorsement by the PostgreSQL project.

## Português (Brasil)

O PGMS usa os seguintes pacotes, com versões resolvidas registradas nos `packages.lock.json`:

| Componente | Versão | Licença/origem |
| --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT — https://github.com/icsharpcode/AvalonEdit |
| AvalonDock, Core, Serializer.Xml, Themes.VS | 5.0.1 | Microsoft Public License (Ms-PL), conforme LICENSE dos pacotes usados — https://github.com/Dirkster99/AvalonDock |
| Npgsql | 10.0.3 | PostgreSQL License — https://github.com/npgsql/npgsql |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT — https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT — https://github.com/dotnet/runtime |
| Anthropic (SDK C#, assistente de IA) | 13.5.0 | Apache-2.0 (MIT nas versões anteriores à 13.0.0; o pacote traz os dois textos) — https://github.com/anthropics/anthropic-sdk-csharp |
| Microsoft.Extensions.AI.Abstractions | 10.5.1 | MIT — https://github.com/dotnet/extensions |

Textos de licença estão em `docs/licenses`. Os ícones simples do shell foram desenhados no próprio XAML. Não foram copiados assemblies ou ícones da instalação do SSMS. Nome de referência e nomes de bibliotecas identificam seus respectivos produtos e autores.

Pacotes self-contained incluem Microsoft.NETCore.App e Microsoft.WindowsDesktop.App, nas versões registradas em PGMS.runtimeconfig.json. Publish copia as licenças e avisos disponíveis dos respectivos runtime packs para `licenses`. O runtime não é componente do SSMS.

O instalador gráfico opcional é gerado com Inno Setup; o motor mantém os créditos do fornecedor e sua licença separada, também instalada como `INNO-SETUP-LICENSE.txt`. Veja [docs/pt-BR/licensing.md](docs/pt-BR/licensing.md).

### PostgreSQL elephant (Slonik)

O ícone e o logo usam o vetor oficial de três cores disponível em https://wiki.postgresql.org/images/a/a4/PostgreSQL_logo.3colors.svg, listado em https://wiki.postgresql.org/wiki/Logo. Geometria preservada; estilos herdados foram explicitados e o símbolo foi redimensionado. A composição PGMS acrescenta o nome do aplicativo e o descritivo, sem sobrepor ou deformar o elefante.

O press kit https://www.postgresql.org/about/press/presskit12/ disponibiliza os logos sob a PostgreSQL License. Texto preservado em docs/licenses/PostgreSQL-logo.txt. O nome PostgreSQL e Slonik são marcas da PostgreSQL Community Association of Canada, sujeitos a https://www.postgresql.org/about/policies/trademarks/. PGMS é um projeto independente e não declara afiliação ou endosso do projeto PostgreSQL.
