# Achievement Labs v1.0.9

This release brings in five open pull requests and a catalogue status cleanup. Download the updated AchievementLabs.exe, or use the in-app update check. Existing settings and saved event tokens are kept.

## Achievement Mirror and Steam fixes ([PR #16](https://github.com/Achievement-Labs/achievement-labs/pull/16), @thebreenis)

- **Achievement Mirror.** A new sidebar section that pairs a Steam game with an Xbox title and copies **new** unlocks in one direction, either Steam → Xbox or Xbox → Steam.
  - Every row has to be matched or marked "No equivalent" before mirroring starts.
  - Achievements already earned before you start are never copied.
  - Each mirrored unlock is read back from the target platform. On failure, mirroring stops and holds later unlocks as pending.
- **Steam achievement saves fixed.** "Steam refused to set achievement" affected v1.0.8 too. Each save now runs in its own helper process and waits for Steam's confirmation.
- **Steam profile at startup.** It now loads at launch, and the Home Steam card no longer shows "Steam not detected" until you click it.
- **Steam library.** Entries without a name use the name from their local schema. The Steam achievements page remembers the last App ID.
- **Not yet live-tested:** Xbox → Steam mirroring.

## Search, Xbox 360 editor and mobile ([PR #15](https://github.com/Achievement-Labs/achievement-labs/pull/15) and [PR #14](https://github.com/Achievement-Labs/achievement-labs/pull/14), @thevampirebat)

- **Title search.** Adds platform filters, and Arabic/Roman numerals now match each other: "Black Ops 3" finds "Black Ops III".
- **Xbox 360 library and achievement editor.** Includes artwork, filtering and sorting, reference-gamertag lookup and Exophase import. "Unlock selected" makes a verified backup and re-signs the working copy.
- **Saved queues.** Each game's queue is restored automatically.
- **Version label.** The sidebar shows the real build version.
- **Android and iOS.** Includes the combined Game Session, the Xbox 360 profile editor and Appearance settings.
- **Update source.** App updates and event JSON for every platform still come from **Achievement-Labs/achievement-labs**. PR #15's change that pointed updates at a fork was not included, and prereleases are still ignored.

## Event catalogue ([PR #12](https://github.com/Achievement-Labs/achievement-labs/pull/12), [PR #17](https://github.com/Achievement-Labs/achievement-labs/pull/17), [PR #18](https://github.com/Achievement-Labs/achievement-labs/pull/18))

- **55 new titles** added as Testing (#17).
- **23 titles marked Supported** (#12).
- **Template fixes** (#18):
  - Quantum Break, Terraria and NHL 18 templates repaired.
  - 67 invalid or empty recipes removed from DOOM, Just Cause 3, Shape Up and NHL 18.
- **Black Ops Cold War removed.** It had no event recipes.
- **Corrected listings.** Metal Gear Solid V: Ground Zeroes, Mega Man Legacy Collection, Mirror's Edge Catalyst and Party Hard are listed as Supported everywhere. DOOM stays Testing.
- **Regenerated title lists.** `docs/title-support.md`, `Events/README.md` and the website list now include every title.

The catalogue now has **512 titles: 289 Supported, 217 Testing and 6 Coming Soon**. `SupportedTitleIDs` is still the final property in both Data.json copies.

## Validation

- **Release build:** succeeds.
- **Enhancement suites:** all pass, including the new `--mirror` suite. The catalogue suite covers 505 local routes, 21,295 recipes and 75,067 JSON payloads.
- **Updater checks:** pass against the canonical repository.
- **Not verified offline:** whether Xbox accepts each unlock.
