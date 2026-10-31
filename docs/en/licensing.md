# Licensing and attribution

English | [Português (Brasil)](../pt-BR/licensing.md)

## Original work

PGMS's original code and documentation are under the [MIT license](../../LICENSE), chosen by the maintainer. Preserve its copyright and permission notice in copies or substantial portions. The canonical license text is English; this guide explains the project arrangement rather than replacing that text. Third-party software, assets and trademarks do not become MIT-licensed PGMS property through this choice.

## Dependencies and assets

Versions below follow the current `packages.lock.json` files. Original license texts remain under [docs/licenses](../licenses) and the original [notices](../../THIRD-PARTY-NOTICES.md) are preserved in Portuguese.

| Component | Version | License / source |
| --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT; [project](https://github.com/icsharpcode/AvalonEdit) |
| AvalonDock/Core/XML serializer/VS themes | 5.0.1 | Ms-PL; [project](https://github.com/Dirkster99/AvalonDock) |
| Npgsql | 10.0.3 | PostgreSQL License; [project](https://github.com/npgsql/npgsql) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT; [runtime](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT; [runtime](https://github.com/dotnet/runtime) |
| .NET/WPF self-contained runtime | package-specific | Licenses and notices copied from runtime packs when packaging; M1002 bundles 10.0.14. |
| PostgreSQL elephant/Slonik | original artwork | Original license in [PostgreSQL-logo.txt](../licenses/PostgreSQL-logo.txt); [press kit](https://www.postgresql.org/about/press/presskit12/) and [trademark policy](https://www.postgresql.org/about/policies/trademarks/). |

The application artwork includes the PostgreSQL elephant. Copyright permission and trademark conditions are separate. No permission to change/combine marks or imply endorsement is granted by the PGMS MIT license. Keep the original artwork provenance; any public branding redesign must follow the PostgreSQL trademark policy. PGMS is an independent application, without endorsement or affiliation with Microsoft or the PostgreSQL project.

## Reference materials

SSMS is a UI reference, not a redistributed implementation. The tracked artifact inventory contains PGMS source/resources, not SSMS binaries. The public screenshot is of PGMS with synthetic data, with [provenance](../assets/README.md); it does not establish the licensing of vendor screenshots. Do not add proprietary assemblies, icons or copied vendor source.

Source builds copy the project LICENSE, third-party notices and both distribution-guide languages into the package. No prebuilt binary release is published by this source import.
