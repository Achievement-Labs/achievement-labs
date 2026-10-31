# Launcher attachment test build

This Windows x64 build supports **Attach launcher session** for Epic Games Launcher, EA app, Ubisoft Connect and GOG Galaxy. Browser sign-in remains available.

## Use

1. Open the desired launcher, sign in, and let its Library finish loading.
2. Open the matching platform Library in Achievement Labs and select **Attach launcher session**.
3. The app verifies the account with the provider, displays the connected account and loads its owned titles, including uninstalled games. **Refresh** checks the launcher account again. **Stop refresh** also cancels attachment.
4. Use the separate Achievements sidebar entry or a game's Open achievements action. Opening a game fetches its achievement catalog and player progress. The game picker, search, status filter and sorting work on this page; **Refresh achievements** retries the selected game. Loading, provider errors and genuinely empty catalogs have explicit messages.
5. Disconnect removes this app's encrypted saved connection; it does not sign out the launcher.

## How attachment works

GOG reads Galaxy's current-user registry session and verifies the account online. Epic, EA and Ubisoft use read-only access to the selected running launcher process, like the existing Xbox attachment. Reads are limited to supported launcher executables owned by the same Windows user in the same Windows session. The app never injects code, changes process memory, enables debug privileges, or saves memory dumps. Candidate sessions are checked only with their provider. Expired candidates are rejected; multiple verified accounts require an explicit fresh launcher sign-in rather than silently choosing one. Saved app sessions are protected with Windows DPAPI.

Owned libraries come from Epic's paginated library API, GOG's paginated releases API, EA's current Juno GraphQL library, and Ubisoft's authenticated ownership service. Ubisoft title names and artwork come from ownership configuration with GamesDB artwork fallback. Installed-game records enrich matching owned titles; they cannot add unowned games. A failed or partial refresh does not replace a previously completed account cache.

Attachment requires the launcher to be running and signed in. Launchers and Achievement Labs must run with compatible Windows access permissions. Provider updates may require adapter changes. The app does not automatically start a launcher when Attach is selected.

## Validation and remaining scope

A separate explicit live check exercises account verification, owned-library retrieval, and up to three cover-image downloads per platform. It saves aggregate counts only. Automated UI checks use fictional sessions and test pagination, deleted/missing product records, account isolation, stale candidates, ambiguous accounts, malformed ownership responses, image decoding, and layout bounds at 900-1440 pixels.

Build 16 adds on-demand EA, Epic and GOG achievement API readers, Ubisoft definition bundles and a read-only Ubisoft native achievement-state adapter. Definitions and verified progress are saved with the matching account cache. Switching games cancels stale loads; failed reads can be retried. Ubisoft cached definitions remain readable when its current session cannot be verified. Unavailable progress remains Unknown and cannot produce a completion badge.

This build does not add online achievement writes or standalone spoofing for these four platforms. EA's current achievement API exposes names, descriptions and progress, but no achievement icons or rarity. No achievement unlocks or game launches are performed by the checks. Live Events/Data.json is unchanged. This is a local test package, not a published official release.

Developer checks: `--launcher-session-check <Epic|EA|Ubisoft|GOG> <aggregate report>` explicitly reads a live launcher session without saving it. `--platform-ui-check --fixtures <folder> --output <folder>` and `--steam-ui-check --output <folder>` use offline fixtures. Build with `tools/Build-PlatformTest.ps1 -HelperDirectory <prepared helpers folder>`.

Library validation on 2026-10-29: EA returned 7 owned titles, Epic 108 and GOG 2; all sampled covers downloaded as valid images. Ubisoft initially returned 65 owned titles with 64 cover URLs. Ubisoft testing resumed after the user signed in again; its currently exposed ticket is rejected with HTTP 401. Native Ubisoft player-progress verification remains open. Achievement live checks retrieved Apex Legends (12), Battlefield V (21), Hogwarts Legacy (45) and The Callisto Protocol (47). The two owned GOG classics, Arena and Daggerfall, return valid empty achievement catalogs. Epic player-progress availability is provider-dependent; missing responses remain Unknown.
