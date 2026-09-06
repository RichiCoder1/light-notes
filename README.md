# Light Notes

A small Windows desktop home for links and plain-text notes, built with Lucent and authored in `.lui`.

Capture a link or thought, select a saved item, edit its title, URL and note, then save or archive it. Records live in a local SQLite database. The note body supports multiple lines, wrapping, selection, clipboard, undo/redo and scrolling through Lucent’s reusable TextArea. The responsive shell adapts from navigation/list/editor to a rail and then a single-pane collection/editor. The next work completes the daily-use workflow. See the [manual review guide](docs/MANUAL-REVIEW.md) for an isolated sample workspace and framework/`.lui` review notes.

## Run locally

Requirements: Windows 11 24H2 or later, x64, PowerShell 7, Git/GitHub CLI, and .NET SDK 10.0.400 (latest servicing patch permitted). NativeAOT publishing additionally needs Visual Studio 2022 or later with Desktop development with C++ and a Windows SDK supporting 10.0.26100.0.

```powershell
git clone https://github.com/RichiCoder1/light-notes.git
cd light-notes
gh auth login
gh auth refresh --scopes read:packages
./tools/Build.ps1 -Run
./tools/Build.ps1 -Publish
```

The GitHub sign-in is one-time setup. GitHub's NuGet registry requires authentication even for public packages. Build.ps1 passes credentials only in the child process environment and restores the prior value afterward; no token belongs in this repository. CI uses GITHUB_TOKEN and needs read access to the Lucent packages.

Run the published application from `artifacts/publish/LightNotes.exe`, retaining its native libraries and notices. A normal window close negotiates host shutdown and service disposal.

## Dependencies and development

Lucent is consumed entirely through prerelease packages. The `.lui` SDK pin in global.json and the runtime pin in Directory.Build.props must match. packages.lock.json records the dependency closure. To upgrade, change both pins, run `./tools/Build.ps1 -UpdateLock -Publish`, and commit the updated pins and lock file together. Do not use floating prerelease versions.

For framework work, pack a unique local version using Lucent's [package instructions](https://github.com/RichiCoder1/lucent/blob/b9d6cbecc9ff410c96451a0e50b68f8345a3382a/docs/PACKAGES.md), point the `lucent` source in NuGet.config at that local folder, and update both pins. Restore needs no prebuilt Lucent checkout DLLs or hidden Debug outputs. Keep local feed paths out of commits.

UI belongs in `src/LightNotes/*.lui`; C# supplies the entry point, models and services. No editor extension is required to build. For VS Code language support, follow the [pinned tooling contract](https://github.com/RichiCoder1/lucent/blob/b9d6cbecc9ff410c96451a0e50b68f8345a3382a/docs/LUI-SDK-TOOLING.md); the language server is a separate development tool.

The owner and coding agents on the owner's machine are the primary development audience. Cross-repository work is tracked initially in [Lucent #62](https://github.com/RichiCoder1/lucent/issues/62) and [the app plan](https://github.com/RichiCoder1/lucent/issues/63). MIT licensed; see [CREDITS](CREDITS.md) for dependency provenance.

## Local data and recovery

Data lives in `%LOCALAPPDATA%/LightNotes/notes.db`. Set `LIGHT_NOTES_DATA_DIRECTORY` to use a separate directory for development or automation. Tests always use temporary databases. Never commit a personal database or export.

Ctrl+N focuses capture; Ctrl+F focuses search; Ctrl+S saves the selected draft. Use Add to capture the entered text. Selecting another item or closing saves the current draft first. A failed save keeps the draft and window available for retry. A successful save means the database transaction committed; it does not imply off-device backup or protection against disk failure.

The Backup button creates a consistent SQLite copy under the data directory's `Backups` folder. For a portable JSON export or an explicitly located backup, close the app and run:

```powershell
./artifacts/publish/LightNotes.exe --export C:/Backups/light-notes.json
./artifacts/publish/LightNotes.exe --backup C:/Backups/light-notes.db
```

Use a new destination filename. Schema migration, format and recovery details are documented in [STORAGE.md](docs/STORAGE.md). Keep the original database and its associated files intact when recovering; restore a backup into a separate data directory first. There is no automatic cloud sync or import merge.

## Tests

Run `./tools/Test.ps1` for temporary-database, workspace and real `.lui` shell geometry/state contracts. After changing package dependencies, use `-UpdateLock` once, then commit the lock files. `./tools/Build.ps1 -Publish` produces the NativeAOT application. Published desktop interaction tests run separately in an interactive Windows session.

### Optional published desktop test

The published persistence test is opt-in and is not included in `./tools/Test.ps1`. After publishing, run the following from the repository root in an interactive Windows session:

```powershell
$env:LIGHT_NOTES_PUBLISHED = (Resolve-Path ./artifacts/publish/LightNotes.exe).Path
dotnet restore ./tests/LightNotes.Desktop.Tests/LightNotes.Desktop.Tests.csproj --locked-mode
dotnet test --project ./tests/LightNotes.Desktop.Tests/LightNotes.Desktop.Tests.csproj -c Release --no-restore
```

The test writes its review screenshot to `artifacts/desktop/light-notes-persistence.png`; set `LIGHT_NOTES_DESKTOP_ARTIFACTS` to override that location.

The desktop suite also checks responsive pane replacement, minimum client size, draft continuity, shortcut/Back focus and targeted Axe.Windows rules. It takes foreground focus and should run only while the PC is available. Offline shell captures can be exported during managed tests by setting `LIGHT_NOTES_REVIEW_ARTIFACTS`; these are renderer fixtures, not native desktop screenshots.
