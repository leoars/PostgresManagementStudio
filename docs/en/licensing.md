# Licensing and attribution

English | [Português (Brasil)](../pt-BR/licensing.md)

## Original work

PGMS's original code and documentation are under the [Apache License 2.0](../../LICENSE), chosen by the maintainer: free for personal and business use. Anyone redistributing PGMS or a work built on it must keep the copyright notices and the attribution in [NOTICE](../../NOTICE), and state which files they changed. Releases through 0.1.0-alpha.4 remain under MIT. The canonical license text is English; this guide explains the project arrangement rather than replacing that text. Third-party software, assets and trademarks do not become MIT-licensed PGMS property through this choice.

## Dependencies and assets

Versions below follow the current `packages.lock.json` files. Original license texts remain under [docs/licenses](../licenses) and the [third-party notices](../../THIRD-PARTY-NOTICES.md) are bilingual (English and Portuguese).

| Component | Version | License / source |
| --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT; [project](https://github.com/icsharpcode/AvalonEdit) |
| AvalonDock/Core/XML serializer/VS themes | 5.0.1 | Ms-PL; [project](https://github.com/Dirkster99/AvalonDock) |
| Npgsql | 10.0.3 | PostgreSQL License; [project](https://github.com/npgsql/npgsql) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT; [runtime](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT; [runtime](https://github.com/dotnet/runtime) |
| .NET/WPF self-contained runtime | package-specific | Licenses and notices copied from runtime packs when packaging; M1008 bundles 10.0.14. |
| PostgreSQL elephant/Slonik | original artwork | Original license in [PostgreSQL-logo.txt](../licenses/PostgreSQL-logo.txt); [press kit](https://www.postgresql.org/about/press/presskit12/) and [trademark policy](https://www.postgresql.org/about/policies/trademarks/). |

The application artwork includes the PostgreSQL elephant. Copyright permission and trademark conditions are separate. No permission to change/combine marks or imply endorsement is granted by the PGMS license. Keep the original artwork provenance; any public branding redesign must follow the PostgreSQL trademark policy. PGMS is an independent application, without endorsement or affiliation with Microsoft or the PostgreSQL project.

## Installer engine

The optional graphical installer uses [Inno Setup 7.1.1](https://jrsoftware.org/isinfo.php). Its engine retains the vendor credits and its separate [license](https://github.com/jrsoftware/issrc/blob/main/license.txt), also installed as `INNO-SETUP-LICENSE.txt`. PGMS's MIT license does not replace the engine's terms. The compiler is a build tool; the application has no runtime dependency on a globally installed compiler.

## Reference materials

SSMS is a UI reference, not a redistributed implementation. The tracked artifact inventory contains PGMS source/resources, not SSMS binaries. The public screenshot is of PGMS with synthetic data, with [provenance](../assets/README.md); it does not establish the licensing of vendor screenshots. Do not add proprietary assemblies, icons or copied vendor source.

Source builds copy the project LICENSE, third-party notices and both distribution-guide languages into the package. The first binary pre-release is v0.1.0-alpha.1; see the release notes for its exact validation.
