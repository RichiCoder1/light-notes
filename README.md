# Light Notes

A small Windows desktop home for links and plain-text notes, built with Lucent and authored in `.lui`.

Capture a link or thought, edit its title, URL and note, and let autosave persist the draft. Find saved items, open web links, and archive or restore notes. Records live in a local SQLite database. The note body supports multiple lines, wrapping, selection, clipboard, undo/redo and scrolling through Lucent’s reusable TextArea. The responsive shell adapts from navigation/list/editor to a rail and then a single-pane collection/editor. See the [manual review guide](docs/MANUAL-REVIEW.md) for an isolated sample workspace and framework/`.lui` review notes.

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

Lucent is consumed entirely through prerelease packages. The `.lui` SDK pin in `global.json` and the runtime pin in `Directory.Build.props` must match. Do not use floating prerelease versions. To upgrade, change both pins, then refresh every maintained project graph:

```powershell
./tools/Test.ps1 -UpdateLock
./tools/Build.ps1 -UpdateLock -Publish
dotnet restore ./tests/LightNotes.Desktop.Tests/LightNotes.Desktop.Tests.csproj --force-evaluate
dotnet restore ./tools/LightNotes.Review/LightNotes.Review.csproj --force-evaluate
```

Together these commands refresh the six maintained lock files under `src/LightNotes`, `src/LightNotes.Storage`, `tests/LightNotes.Tests`, `tests/LightNotes.Storage.Tests`, `tests/LightNotes.Desktop.Tests`, and `tools/LightNotes.Review`. Review all six and commit both pins with every changed lock file. `-UpdateLock` temporarily uses a fresh NuGet HTTP cache so the SDK resolver sees a newly published SDK version; normal builds retain the usual cache. Vendored Lucent fixtures are not part of the Light Notes package upgrade.

For framework work, pack a unique local version using Lucent's [package instructions](https://github.com/RichiCoder1/lucent/blob/10d03b74089de05cda340f6301014bceb9507754/docs/PACKAGES.md), point the `lucent` source in NuGet.config at that local folder, and update both pins. Restore needs no prebuilt Lucent checkout DLLs or hidden Debug outputs. Keep local feed paths out of commits.

Open the repository root in VS Code; the checked-in settings select `LightNotes.slnx`, containing the app and managed tests. This keeps vendored Lucent package-test fixtures out of automatic project discovery. Desktop tests remain opt-in through their explicit project. VS Code also needs NuGet feed credentials available outside Build.ps1; configure the `lucent` source in your user-level NuGet.Config with Windows-encrypted credentials, then reload the window.

UI and mount-owned presentation state belong in `src/LightNotes/*.lui`; C# supplies the entry point, application models and services. CollectionPane owns derived presentation and its Clear Search handler; NoteRow owns its temporary menu-open cue. The workspace owns per-collection browsing continuity, drafts, accepted writes, and cross-pane focus so they survive presentation changes. No editor extension is required to build. For VS Code language support, follow the [pinned tooling contract](https://github.com/RichiCoder1/lucent/blob/10d03b74089de05cda340f6301014bceb9507754/docs/LUI-SDK-TOOLING.md); the language server is a separate development tool.

The owner and coding agents on the owner's machine are the primary development audience. Cross-repository work is tracked initially in [Lucent #62](https://github.com/RichiCoder1/lucent/issues/62) and [the app plan](https://github.com/RichiCoder1/lucent/issues/63). MIT licensed; see [CREDITS](CREDITS.md) for dependency provenance.

## Local data and recovery

Data lives in `%LOCALAPPDATA%/LightNotes/notes.db`. Set `LIGHT_NOTES_DATA_DIRECTORY` to use a separate directory for development or automation. Tests always use temporary databases. Never commit a personal database or export. If the app exits because of an unhandled error, it writes best-effort diagnostics to `last-crash.txt` beside `notes.db`, replacing the previous report; the report records the app version, exception types and stack traces without note contents.

Ctrl+N focuses capture; Ctrl+F focuses search; Ctrl+S saves the selected draft. Use Add to capture the entered text. Edits autosave after a 750 ms pause while typing stays enabled. Save now/Ctrl+S saves immediately. Navigation preserves pending edits through the workspace writer, and closing waits for accepted writes. Incomplete titles and addresses use a separate durable recovery draft; Discard draft restores the last valid saved note. Inbox and Archive retain their own selected note, search and scroll position. Right-clicking a text field or note body preserves its selection and offers Undo, Redo, Cut, Copy, Paste, and Select all with state-based enablement. Invoking a command or pressing Escape closes the popup and returns focus to the editor. Right-clicking a different note keeps the current note open until Open note is invoked; Archive/Restore and Open web address act on the menu target. Open beside the web address launches the current valid HTTP/HTTPS link in your default browser. A failed save keeps the draft and window available for retry. A successful save means the database transaction committed; it does not imply off-device backup or protection against disk failure.

The Backup button creates a consistent SQLite copy under the data directory's `Backups` folder. For a portable JSON export or an explicitly located backup, close the app and run:

```powershell
./artifacts/publish/LightNotes.exe --export C:/Backups/light-notes.json
./artifacts/publish/LightNotes.exe --backup C:/Backups/light-notes.db
./artifacts/publish/LightNotes.exe --restore C:/Backups/light-notes.db C:/Recovered/LightNotes
```

Use new destination filenames for export/backup and a new directory for restoration. The restore command validates the backup and creates `notes.db` there; it does not open or replace the default live database. Set `LIGHT_NOTES_DATA_DIRECTORY` to the restored directory to review it. Schema migration, format and recovery details are documented in [STORAGE.md](docs/STORAGE.md). Keep the original database and its associated files intact when recovering; restore a backup into a separate data directory first. There is no automatic cloud sync or import merge.

## Tests

Run `./tools/Test.ps1` for temporary-database, workspace and real `.lui` shell geometry/state contracts. After changing package dependencies, use `-UpdateLock` once, then commit the lock files. `./tools/Build.ps1 -Publish` produces the NativeAOT application. Published desktop interaction tests run separately in an interactive Windows session.

### Optional published desktop test

The published persistence test is opt-in and is not included in `./tools/Test.ps1`. After publishing, run the following from the repository root in an interactive Windows session:

```powershell
$env:LIGHT_NOTES_PUBLISHED = (Resolve-Path ./artifacts/publish/LightNotes.exe).Path
dotnet restore ./tests/LightNotes.Desktop.Tests/LightNotes.Desktop.Tests.csproj --locked-mode
dotnet test --project ./tests/LightNotes.Desktop.Tests/LightNotes.Desktop.Tests.csproj -c Release --no-restore
```

The desktop suite covers autosave/reopen, responsive focus and targeted accessibility scans, long-note wheel/thumb scrolling with repeated archive/restore, native cursor/caret phases, invalid-draft navigation/reopen/discard, and note-row and text-editor menu dismissal, command routing, focus return, and owner-close lifetime. It runs sequentially with temporary synthetic data and takes foreground focus. The persistence test verifies the database before closing so close-time saving cannot hide an autosave regression. Captures and failure diagnostics go to `artifacts/desktop`; set `LIGHT_NOTES_DESKTOP_ARTIFACTS` to override that location. Unexpected-exit reports are copied out before temporary test data is removed.

The desktop suite also checks responsive pane replacement, minimum client size, draft continuity, shortcut/Back focus and targeted Axe.Windows rules. It takes foreground focus and should run only while the PC is available. Offline shell captures can be exported during managed tests by setting `LIGHT_NOTES_REVIEW_ARTIFACTS`; these are renderer fixtures, not native desktop screenshots.

For the local-state declaration rules and next review questions, see [component ownership](docs/COMPONENT-OWNERSHIP.md).
