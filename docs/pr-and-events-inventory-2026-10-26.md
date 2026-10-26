# PR and events inventory (2026-10-26)

Read-only review. Nothing has been merged, pushed or edited in Data.json. Local `main` was fast-forwarded to `origin/main` (b4c5a64, which includes PR #13).

## Open PRs

| PR | Author | Draft | CI | What it does | Merge notes |
|---|---|---|---|---|---|
| #18 Repair six event titles | ethanwp28 (Codex) | yes | pass | Quantum Break template insertion points restored. Terraria ach. 23 token fixed. NHL 18: 2 recipes for another title removed. Removes 65 empty recipe stubs from DOOM, Just Cause 3 and Shape Up. Renderer: 87 failures → 0. | Clean on main and alongside #12/#17. Changes no support flags. Quarantines 67 mappings, so DOOM and Shape Up lose those achievements. |
| #17 Add 55 Testing titles | ethanwp28 (Codex) | yes | pass | Adds 55 new title blocks and templates as Testing (Enabled=true, FullySupported=false). | Clean merge, but Data.json grows to about 94.85 MiB, close to GitHub's 100 MiB hard limit. Any later title additions will need the catalogue split or moved to LFS or release assets. |
| #16 Achievement Mirror | thebreenis | no | **none ran** | New Steam↔Xbox mirror. Also fixes Steam achievement saves, which are broken in v1.0.8, using a SteamIdle helper process. Version label read from the build. Steam profile loads at startup. Steam library names taken from the local schema. | Conflicts with #15 in DesktopModel.cs, MainWindow.axaml and Test-Enhancements.ps1. Xbox→Steam direction not live-tested. Stores the user's Steam Web API key with DPAPI. CI hasn't run because it's a fork PR, so approve the workflow run. |
| #15 Search, mobile Game Session, Xbox 360 signed editing | thevampirebat | no | pass | Large feature batch (100+ files): platform search filters, Xbox 360 profile editor on Windows/Android/iOS with re-signing, mobile Game Session and appearance redesign, Exophase import. | **Blocker:** points the app-update feed for Windows, Android and iOS at `thevampirebat/achievement-labs` (Updater.java, AppUpdater.cs, iOS). Official builds would install binaries from a third-party fork. Ask for that change to be reverted. It already contains #14's search code (TitleSearchText.cs is identical). |
| #14 Platform filters and numeral search | thevampirebat | yes | pass | Search filters plus Roman/Arabic numeral aliases. | Its description says to keep it in draft and not merge. #15 supersedes it, so close it if #15 lands. |
| #12 Mark 23 titles Supported | ethanwp28 (Codex) | yes | none | Flips 19 titles Testing→Supported; the other 4 were already Supported. | Base is 12 commits behind but merges cleanly. Its description says the live licensing DB was **already updated** for these 23, so the deployed service and the repo currently disagree until this merges. |

Trial merges on `origin/main`: #18 + #12 + #17 apply cleanly together. #15 merges cleanly alone, but #16 conflicts after it.

Stale local branches: `codex/event-loops-20261026` has nothing beyond main. `release-prepared-v1.0.2` is old.

## Events catalogue (origin/main)

458 title blocks: **270 Supported**, **182 Testing**, **6 Disabled/Coming Soon** (Enabled=false). `SupportedTitleIDs` is last ✅. `Events/Data.json` and `src/AchievementLabs.App/Events/Data.json` are identical ✅. `docs/title-support.md` is stale (413 titles / 263 / 147 / 3).

After the event PRs: #12 → 289 Supported / 163 Testing. #17 adds 55 Testing titles (513 total). #18 changes no flags.

### Status issues to decide

| Title ID | Title | Problem |
|---|---|---|
| 1790482525 | CoD: Black Ops Cold War (Series X\|S) | Block has 0 mappings, no template, and isn't in SupportedTitleIDs, yet it's flagged Testing (docs say Coming Soon). Should be Coming Soon (Enabled=false) or removed. |
| 515050978 | (unnamed) | 0 mappings, disabled, still listed in SupportedTitleIDs. |
| 153292310 | Disney Infinity 3.0 | Data says Testing, docs say Coming Soon. |
| 650249872 | MGS V: Ground Zeroes | Data says Supported, docs say Testing. |
| 1535750831 | Mega Man Legacy Collection | Data says Supported, docs say Testing. |
| 1824138520 | Mirror's Edge Catalyst | Data says Supported, docs say Testing. |
| 916339986 | Party Hard | Data says Supported, docs say Testing. |
| 55549300 | Axiom Verge | Disabled in data, Testing in docs. |
| 1327376519 | DOOM | 52 of 54 recipes empty but marked Testing. Fixed by #18, which leaves 2 mappings, so likely Partial. |
| 1147540192 | Shape Up | 12 empty recipes (fixed by #18). |
| 1175562227 | Just Cause 3 | Supported with 1 empty recipe (fixed by #18). |

There is no "Partially supported" flag in Data.json: only `FullySupported` true/false and `Enabled`. "Partial" is currently folded into Testing. Distinguishing it needs either a new field (app and service change) or a docs-only label.

45 title IDs in Data.json are missing from `docs/title-support.md`, for example 666757656, 1787967632, 711381438 and 875079140. The docs table should be regenerated after the merges.

### Untracked working files (not committed)

- `Events/tapping skill test.json` (and the src copy) is a captured telemetry event for T875079140, not a template, so it shouldn't ship.
- `Events/Data.json.before-merge-backup-20260629223709.json` (×2) is an old backup.
- `docs/automatic-update-plan.md`, `docs/stat-editor-audit.md`, `tools/ghidra/`, `tools/GPSaveConverter-Profiles/` are Codex leftovers to review.
