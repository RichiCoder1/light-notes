# Credits

## Storage

- [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.11), version 10.0.11, provides the maintained low-level SQLite ADO.NET provider. Light Notes owns its schema, migrations, parameterized SQL, ordering, close coordination, backup, and recovery policy. Microsoft licenses the package under the MIT License; SQLite is public domain. Its transitive SQLitePCLRaw 2.1.12 provider and native packaging use Apache-2.0 (SourceGear, LLC). Storage notices ship in the published `notices` folder.

Light Notes is an independent first-party consumer of [Lucent](https://github.com/RichiCoder1/lucent), with an exact experimental NuGet version recorded in global.json and Directory.Build.props. Light Notes and Lucent use the MIT license; ecosystem packages keep their own terms.

Lucent's [dependency ledger](https://github.com/RichiCoder1/lucent/blob/a83761967f5f42d76179079c7b5129f897b5481e/CREDITS.md) identifies SkiaSharp/HarfBuzzSharp, SDL3-CS/SDL3, Svg.Skia's secure SVG adapter, .NET, the VC runtime, Microsoft.Extensions.Hosting, and build-only Roslyn dependencies. Package targets retain runtime notices in published output. The app uses installed system fonts, ships the original `src/LightNotes/Artwork/light-notes.svg` application artwork under the repository license, and consumes the separately licensed Lucide artwork package with its upstream notices.

The composition uses Lucent's standard control theme with application-owned presentation tokens. Product design follows the [links-and-notes experience](https://github.com/RichiCoder1/lucent/tree/b69ade6/docs/design/links-and-notes); the daily-use workflow continues after the responsive-shell review.

## Test tooling

[FlaUI.UIA3 5.0.0](https://github.com/FlaUI/FlaUI) (MIT) drives the optional published desktop checks through Windows UI Automation. It follows Lucent's existing test-driver choice and is not shipped with Light Notes. MSTest 4.4 supplies the maintained Microsoft test runner.

[Axe.Windows 2.4.2](https://github.com/microsoft/axe-windows) (MIT) supplies targeted automated accessibility rule scans in the opt-in desktop test suite. It is not shipped with the app.

## Reactive scheduling

[R3 1.3.1](https://github.com/Cysharp/R3) (Cysharp, MIT) provides the quiet-period debounce through Lucent's optional `Lucent.Reactive.R3` package. The integration uses an explicit TimeProvider, marshals callbacks to the workspace owner, and cancels pending callbacks with that lifetime. The app retains ownership of accepted writes and uses a small scheduler interface for deterministic workflow tests. This does not replace Lucent's reactive graph.
