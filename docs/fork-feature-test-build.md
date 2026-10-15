# Historical local test-build notes

Superseded for release preparation by [v1.0.7 notes](release-v1.0.7.md): the owner authorized syncing the current 442-title F-drive catalogue into both repo copies and the release. The 413/441 counts below describe earlier test builds.

# Fork feature integration test build

This development build integrates the app features from thevampirebat's cumulative fork. Build Queue, authorization renewal, stat editor, Xbox 360 profile support and the upstream executable update feed are retained.

## Catalogue boundaries

The author-maintained catalogue remains authoritative. Only Dishonored Definitive Edition Xbox One (1100121502) and Call of Duty: Ghosts Xbox One (572802557) receive recipe/identity changes. Minecraft Xbox One Edition (345903534) uses the original hosted service, without a borrowed Windows/Bedrock fallback. No Infinite Warfare, Modern Warfare Remastered, Escapists, Prey or other fork remaps are enabled.

The source repo contains its existing catalogue with only those two corrected blocks. The portable testing build embeds a frozen copy of the newer 441-title `F:\Events` catalogue, with the same two approved corrections. Unrelated live blocks and comments were preserved and backed up. Build with `tools/Build-Release.ps1 -EventsSource <author-catalogue-folder>` to choose the authoritative build snapshot; ordinary source builds default to `src/AchievementLabs.App/Events`. A configured Events directory takes precedence when it contains Data.json. A present but malformed local catalogue fails closed rather than silently replacing it.

Automatic event updates fetch additions from the original upstream repository at startup and hourly. Every existing numeric local title block is reserved, including disabled/incomplete titles. Newly accepted titles are validated, kept in a separate cache and labelled Testing. Neither automatic nor manual checks replace local recipes. Existing cached additions are not silently revised. DLC grouping and shared-total updates contain display metadata and do not change event payloads.

Dishonored includes the approved 80-ID correction and the fork's eight direct criteria: two user-reported successes and six further candidates. Those reports do not independently verify the other candidates. Ghosts now uses Xbox IDs 1–50 and 52–92; ID 51 is absent.

## Features to test

1. **Settings and keyboard:** Save all settings at the top; grouped options; remember size/position; maximize on launch; restart and multi-monitor recovery. Enter performs applicable searches, token saving, single-game queue building or Build Queue Add game. It never starts unlocking or spoofing.
2. **Achievement browsing:** A–Z/Z–A sorting, pack filter, collapsible base/DLC/update sections, search and clear controls. Unknown groups remain Unclassified. Filtering/collapsing must retain appropriate selections.
3. **Selection and retry:** Unlock/retry for mapped event achievements, Select multiple, Select all eligible and Clear. Requests run sequentially; unmapped entries and legacy workflows retain their restrictions.
4. **Library and completion:** remembered A–Z/Z–A/Last Played; selection retention; missing-total scans; separate legacy definition and earned progress; base-completion icons versus whole-list backgrounds; color/toggle persistence. Partial metadata must not invent a verified total.
5. **Exports:** Settings > Export all achievements; progress, pause/resume, restart checkpoints, skip-saved default and refresh. Verified-total exports omit account identifiers, tokens and earned counts.
6. **Single-game queue:** build/load without starting, edit delays, remove selected/completed, save and reopen. Status/countdown/ETA, green completed rows and scrolling should update. Optional failure alerts, stop-on-failure, spoof-failure stop and completion alerts should respect their toggles.
7. **Build Queue:** add several games, add while running/idle, stop/restart/resume, preserve accepted payload checkpoints and inter-title buffers. Failed games retain explicit retry behavior. Standalone spoofing cannot start during a multi-title run.
8. **Spoofer:** recorded playtime, live timer including more than 24 hours, heartbeat diagnostics, concurrent single-game queue ownership, manual-stop suppression and unexpected-stop alerts. Native notification activation should open the app.
9. **Tokens:** Paste and save a current event token in Settings. Tokens remain masked unless revealed; periodic authorization renewal and expiry checks remain available.
10. **Catalogue:** automatic/manual new-title checks preserve all existing mappings. Test Dishonored/Ghosts and legacy Minecraft separately. Collector acceptance is submission evidence; refresh achievement progress to verify actual credit.

## Verification

The integrated enhancement suite covers actual-window initialization and 546 scrolling targets, selection, Ghosts IDs, blocked unapproved remaps, public metadata/cache behavior, completion, exports, tokens, presence gates and notifications. The maintained catalogue payload sweep parses every usable payload as strict JSON and checks title identity. Existing automation checks cover token freshness, Build Queue lifecycle/handoff and stat-catalogue preservation. Native UI smoke checks render all pages and exercise protected storage, filtering and offline workflows. No live Xbox achievements are submitted by these checks.

The executable is a local testing build; it is not published. Live Xbox credit, Windows notification delivery and actual presence remain for the user's testing.

## Attribution and future PRs

Feature reference: [thevampirebat/achievement-labs, v1.0.6-fork.3](https://github.com/thevampirebat/achievement-labs/tree/v1.0.6-fork.3). The enhancement adapters and public display metadata retain their source structure; local workflow integration and catalogue ownership differ intentionally.

The fork owner should create a branch from a current upstream baseline, push the branch to their own fork, then open a pull request with base `ethanwp28/achievement-labs:main` and head `thevampirebat/achievement-labs:<feature-branch>`. They need write access to their own branch, not to the upstream repo. Prefer a separate PR for each app feature and each catalogue correction; include tests and distinguish structurally validated mappings from confirmed live credit. Publishing a fork release does not create an upstream PR. See [GitHub's fork PR instructions](https://docs.github.com/en/pull-requests/how-tos/create-pull-requests/creating-a-pull-request-from-a-fork).
