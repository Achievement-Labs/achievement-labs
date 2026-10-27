# Achievement Labs

Achievement, stat, presence, and save tooling for Windows PC and Xbox titles. The
desktop client uses Avalonia and includes Xbox, Xbox 360, Games for Windows Live,
Steam, Epic, and Ubisoft workflows.

**[Download the latest release](https://github.com/ethanwp28/achievement-labs/releases/latest)**

Windows only. Editing achievement and stat data can carry account risk on a live
service. Use test accounts and keep the backups created by the application.

## Mobile apps

Android and iOS development now lives in [Achievement Labs Mobile](https://github.com/Achievement-Labs/achievement-labs-mobile). Desktop sign-in/QR transfer and the public event feed remain here. See [migration notes](docs/mobile-repository-split.md) for release continuity and pending PRs.

## Build from source

Mac and Linux previews use separate platform builds from this desktop repository.
The Windows build below keeps its existing defaults. See [Linux preview](docs/linux-preview.md)
and [Mac preview](docs/macos-preview.md) for their independent packages and platform requirements.

Install the .NET 9 SDK, then run:

```powershell
dotnet restore .\AchievementLabs.sln
dotnet build .\src\AchievementLabs.Desktop\AchievementLabs.Desktop.csproj -c Release
```

Create the self-contained single-file release candidate with:

```powershell
pwsh -File .\tools\Build-Release.ps1 -Version 1.0.8
```

The release script builds the Avalonia desktop application, the x86 GFWL helper,
and the Steam idle helper. Its output stays under `releases/` and is not committed.

## Repository layout

- `src/AchievementLabs.Desktop/` — native Avalonia desktop client.
- `src/AchievementLabs.Core/` — shared Xbox, Steam, legacy, and catalog logic.
- `src/AchievementLabs.App/` — shared models, services, event templates, and stat catalogs used by the Avalonia client and hosted event service.
- `src/AchievementLabs.App/Events/` — current event catalog, `Data.json`, and one standalone template per title.
- `src/AchievementLabs.App/StatsCatalog/` — title and event stat classifications.
- `server/licensing/` — self-hosted event catalog and account service.
- `site/assets/games.json` — generated public game catalog embedded in the desktop client.
- `tools/` — release and catalog build tools required by the current application.
- `SteamAchievementManager-7.0.41/` — vendored upstream dependency with its license retained.

## Event support states

- **Supported**: mappings have been tested and are expected to work.
- **Testing**: the title has a usable template and mappings, but achievement IDs may
  not match and individual unlock requests may fail. Verify results one at a time.
- **Partially Supported**: some mappings have been tested and are expected to work;
  the rest are still being tested. Verify results one at a time.
- **Coming Soon**: no usable testing template is currently available.

In `Data.json` a title block is Supported when `FullySupported` is `true`, Partially
Supported when `FullySupported` is `false` and the optional `PartiallySupported` is `true`,
Testing otherwise, and Coming Soon when `Enabled` is `false`. Builds that predate
`PartiallySupported` ignore it and show those titles as Testing.

`SupportedTitleIDs` must remain the final top-level property in every `Data.json`.
Each event title must use its own `Events/<titleId>.json` template.

## Local and private data

Authentication files, event tokens, service credentials, databases, research,
packet captures, build output, save backups, and machine-specific agent settings
are excluded through `.gitignore`.

## Support and credits

Questions and test results: [Achievement Labs Discord](https://discord.gg/EY6AJpNfVu)

Achievement Labs builds on ideas and prior work from the original Xbox Achievement
Unlocker project and includes Steam Achievement Manager code under its retained
upstream license.
