# Dependências de terceiros

O PGMS usa os seguintes pacotes, com versões resolvidas registradas nos `packages.lock.json`:

| Componente | Versão | Licença/origem |
| --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT — https://github.com/icsharpcode/AvalonEdit |
| AvalonDock, Core, Serializer.Xml, Themes.VS | 5.0.1 | Microsoft Public License (Ms-PL), conforme LICENSE dos pacotes usados — https://github.com/Dirkster99/AvalonDock |
| Npgsql | 10.0.3 | PostgreSQL License — https://github.com/npgsql/npgsql |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT — https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT — https://github.com/dotnet/runtime |

Textos de licença estão em `docs/licenses`. Os ícones simples do shell foram desenhados no próprio XAML. Não foram copiados assemblies ou ícones da instalação do SSMS. Nome de referência e nomes de bibliotecas identificam seus respectivos produtos e autores.

Pacotes self-contained incluem Microsoft.NETCore.App e Microsoft.WindowsDesktop.App, nas versões registradas em PGMS.runtimeconfig.json. Publish copia as licenças e avisos disponíveis dos respectivos runtime packs para licenses. O runtime não é componente do SSMS.

## PostgreSQL elephant (Slonik)

O ícone e o logo usam o vetor oficial de três cores disponível em https://wiki.postgresql.org/images/a/a4/PostgreSQL_logo.3colors.svg, listado em https://wiki.postgresql.org/wiki/Logo. Geometria preservada; estilos herdados foram explicitados e o símbolo foi redimensionado. A composição PGMS acrescenta o nome do aplicativo e o descritivo, sem sobrepor ou deformar o elefante.

O press kit https://www.postgresql.org/about/press/presskit12/ disponibiliza os logos sob a PostgreSQL License. Texto preservado em docs/licenses/PostgreSQL-logo.txt. O nome PostgreSQL e Slonik são marcas da PostgreSQL Community Association of Canada, sujeitos a https://www.postgresql.org/about/policies/trademarks/. PGMS é um projeto independente e não declara afiliação ou endosso do projeto PostgreSQL.
