# Light Notes

A small Windows desktop home for links and plain-text notes, built with Lucent and authored in `.lui`.

This is the initial independent application setup. It opens and closes a native window through Lucent's Microsoft hosting integration. Capture, storage, the responsive inbox, and multiline editing are still being built; this version does not save notes.

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
