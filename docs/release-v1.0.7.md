# Achievement Labs v1.0.7

This release brings the latest app features and the author's current event catalogue together in a self-contained Windows executable. It follows v1.0.6 and includes the cumulative changes from the recent local test builds.

## Build Queue and auto unlockers

- Add multiple Xbox titles to a persistent Build Queue, including while the worker is running or idle. Configure a buffer between titles, and explicitly start, pause, resume or retry jobs.
- Save account-bound job manifests and achievement checkpoints. Interrupted jobs restore paused; failed jobs remain available for explicit retry. Credentials are not stored in queue manifests.
- Checkpoint accepted event payloads so resuming does not deliberately replay an already accepted prefix. Ambiguous transport failures retain an explicit retry decision.
- Keep the single-game queue, with editable delays, manual delay saving, removal of selected/completed entries, saved sessions, status, countdown and remaining-time estimates. Completed rows are green; delay editing is disabled during runs while the list remains scrollable.
- Give Steam its own Auto Unlocker child page under Steam, with separate navigation, queue state and progress totals.
- Enter can build a single-game queue or add a Build Queue job. It does not start unlocking or spoofing.

## Achievement browsing and library

- Move achievement sorting into the All achievements / Locked / Unlocked toolbar next to Open Auto Unlock. Sort by name or Global % in either direction.
- Display Global % from Xbox's achievement rarity data: the share of players of that title who earned the achievement. Missing values remain unknown and sort last; zero remains a valid value.
- Load achievement artwork when Xbox provides it, with a fallback when unavailable.
- Group base game, DLC and title-update achievements into collapsible sections, with pack filtering, search, clear controls and an Unclassified group when metadata is incomplete.
- Add multiple selection, Select all eligible, Clear, and sequential unlock/retry for supported mapped event achievements.
- Remember library A–Z, Z–A or Last Played sorting, preserve selected titles through refresh, and avoid forced scrolling after sorting.
- Add Copy ID, Open achievements and Open stats to library rows.
- Improve missing achievement-total scans, base-game completion icons, whole-list completion backgrounds and configurable completion colours. Display metadata does not invent verified totals or change event recipes.

## Tools and exports

- Add Tools → Store link to Title ID. Paste a Microsoft/Xbox Store URL or a 12-character product ID, press Enter or Look up Title ID, then copy the decimal Xbox Title ID or open achievements.
- Look up Microsoft's catalogue mapping rather than attempting a numeric conversion. Products without a published mapping, including some bundles/add-ons, report that limitation. Store display names can differ from achievement-list names.
- Add Settings → Export all achievements with progress, pause/resume, restart checkpoints, skip-saved defaults and explicit refresh. Shared verified-total exports omit account identifiers, tokens and earned counts.
- Retain v1.0.6 Xbox 360 profile editing, GFWL, verified Title Search and Windows 8 achievement-reading workflows.

## Stats editor

- Open the stats editor directly from a library title.
- Show title-based versus event-based stats independently of achievement support.
- Determine event-stat support from Stats.json and the title's standalone event template. Missing or disabled mappings remain unsupported.
- Validate mapped event-stat payloads, title identity and connected account before submission. Request acceptance is not presented as independent proof that a stat changed.

## Presence, connection and notifications

- Maintain Xbox authorization periodically and before applicable queue requests/heartbeats, validating that renewal retains the connected account. Preserve rotated OAuth refresh credentials in supported saved sessions.
- Improve presence heartbeats, elapsed-time display beyond 24 hours, recorded-playtime display and read-only diagnostics.
- Allow standalone presence with the single-game queue while preserving ownership rules; block conflicting multi-title runs. Presence cleanup failures stop progression to the next title.
- Add configurable failure, spoof-stop and completion notifications, with manual-stop suppression and Windows notification activation that opens the app.
- Keep event-token entry manual in Settings, masked unless revealed, with Windows-user protected storage, account checks and expiry/rejection status. Waiting queues preserve their checkpoints until a usable replacement is saved or the user stops.
- Remove automatic event-token grabber buttons, preferences, background retrieval and experimental cache/Windows-broker retrieval. No Python token-capture script is bundled.

## Settings and window behaviour

- Group settings and provide Save all settings at the top.
- Add Always open maximized, enabled by default for fresh settings. Respect an explicitly saved off preference.
- Remember window size, position and monitor placement, with recovery for changed monitors and display scaling.
- Improve Enter-key handling for applicable searches and token saving without implicitly starting an unlock/presence run.
- Correct the project GitHub link to https://github.com/ethanwp28/achievement-labs/.
- Remove the Legacy tools page while retaining dedicated Xbox 360, GFWL and legacy achievement-reading workflows.

## Event catalogue and mapping changes

- Synchronize the current author-maintained F:\Events catalogue into both repository Events directories and embed that same catalogue in this release: **442 title blocks: 267 Supported, 169 Testing and 6 Coming Soon**. The accompanying supported-titles.csv lists each title, platform and support classification.
- Include the author's latest title additions, existing-title recipe/template updates, Stats.json and XboxTitleIDs.json. Preserve author classifications and keep SupportedTitleIDs last in Data.json.
- Include the approved Dishonored Definitive Edition Xbox One achievement/criteria corrections and Call of Duty: Ghosts Xbox One ID corrections. Minecraft Xbox One Edition uses the original hosted-service route.
- Keep other fork-specific edition-remapping aliases disabled. Catalogue synchronization uses the author's source, not a replacement fork catalogue.
- Automatically check for new upstream event titles at startup and periodically, with a manual check available. Validate additions, label them Testing and keep them in a separate cache. Existing local mappings, including disabled/incomplete titles, are reserved and are not overwritten.
- Shared DLC and achievement-total updates affect display metadata, not event payloads. Existing local Events overrides retain precedence; a malformed local catalogue fails closed.
- The executable embeds its catalogue; it does not extract a loose Events folder. An optional Events.zip contains the matching clean catalogue for manual/offline use. Backups, research files and credentials are excluded.

## Repository and distribution

- Remove bot implementation, operational documentation and bot-specific ignore entries from the current repository tree. Git history is retained.
- Add contribution guidance for separate feature and catalogue pull requests, with validation evidence and a distinction between structural checks and confirmed live credit.
- Retain the upstream executable update feed introduced in v1.0.6. Publish AchievementLabs.exe and the matching update.json together; a SHA-256 checks integrity, not publisher identity.
- Thanks to [@thevampirebat](https://github.com/thevampirebat) for the work in [their Achievement Labs fork](https://github.com/thevampirebat/achievement-labs), reviewed through [v1.0.6-fork.3](https://github.com/thevampirebat/achievement-labs/releases/tag/v1.0.6-fork.3). This release adapts their achievement sorting/grouping and selection, queue controls, export, library/completion, notification, settings and display-metadata improvements to the upstream app. The approved Dishonored/Ghosts mapping corrections also draw on that work. Catalogue authority and account/workflow safeguards remain with this project. Existing dependency licences are retained.

## Every title added since v1.0.6

These **42** new title blocks are listed with their release classification; additions are not all Supported.

| Title | Platform | Title ID | Status |
|---|---|---|---|
| Age of Empires®: Castle Siege | Windows PC | `614295058` | Testing |
| Battleborn | Xbox One, Xbox Series X\|S | `1863651407` | Testing |
| DOOM | Xbox One, Xbox Series X\|S | `1327376519` | Testing |
| EA SPORTS™ NBA LIVE 15 | Xbox One, Xbox Series X\|S | `419391420` | Testing |
| EA SPORTS™ NBA LIVE 16 | Xbox One, Xbox Series X\|S | `2067761315` | Testing |
| EA SPORTS™ NHL® 16 | Xbox One, Xbox Series X\|S | `242301355` | Testing |
| EA SPORTS™ NHL™ 17 | Xbox One, Xbox Series X\|S | `492699383` | Coming Soon |
| EA SPORTS™ UFC® | Xbox One, Xbox Series X\|S | `548464799` | Testing |
| EA SPORTS™ UFC® 2 | Xbox One, Xbox Series X\|S | `552778408` | Testing |
| EA SPORTS™ UFC® 3 | Xbox One, Xbox Series X\|S | `722006140` | Testing |
| EVERSPACE™ | Windows PC | `968775939` | Testing |
| FINAL FANTASY TYPE-0 HD | Xbox One, Xbox Series X\|S | `1079518187` | Supported |
| FINAL FANTASY TYPE-0 HD (Asian version) | Xbox One, Xbox Series X\|S | `708445180` | Supported |
| FINAL FANTASY TYPE-0™ HD | Xbox One, Xbox Series X\|S | `585022010` | Supported |
| FINAL FANTASY XV MULTIPLAYER: COMRADES | Xbox One, Xbox Series X\|S | `136671143` | Supported |
| FINAL FANTASY XVI | Windows PC, Xbox Series X\|S | `1663711933` | Supported |
| FINAL FANTASY 零式 HD | Xbox One, Xbox Series X\|S | `1342420348` | Supported |
| Grand Theft Auto Online (Xbox Series X/S) | Xbox Series X\|S | `1778348973` | Testing |
| Guitar Hero Live | Xbox One, Xbox Series X\|S | `453673951` | Testing |
| METAL GEAR SOLID V: THE PHANTOM PAIN | Xbox One, Xbox Series X\|S | `148020354` | Testing |
| METAL GEAR SURVIVE | Xbox One, Xbox Series X\|S | `888477147` | Testing |
| NBA LIVE 14 | Xbox One, Xbox Series X\|S | `1616270757` | Coming Soon |
| NBA LIVE 18: The One Edition | Xbox One, Xbox Series X\|S | `83029588` | Testing |
| NHL® 15 Full Game | Xbox One, Xbox Series X\|S | `1907337607` | Testing |
| NHL™ 94 REWIND | Xbox One, Xbox Series X\|S | `1634565765` | Testing |
| Overcooked | Xbox One, Xbox Series X\|S | `340920273` | Testing |
| Peggle 2 | Xbox One, Xbox Series X\|S | `848717329` | Testing |
| Project Spark | Xbox One, Xbox Series X\|S | `1624350228` | Testing |
| Rayman Legends | Xbox One, Xbox Series X\|S | `666757656` | Testing |
| Rise of Nations: Extended Edition | Windows PC | `326835485` | Testing |
| Rock Band 4 | Durango | `767263759` | Testing |
| Shape Up | Xbox One | `1147540192` | Testing |
| Slender: The Arrival (2015) | Xbox One, Xbox Series X\|S | `1166935255` | Testing |
| Slime Rancher | Windows PC, Xbox One, Xbox Series X\|S | `625948041` | Testing |
| SUBJECT 13 | Windows PC, Xbox One, Xbox Series X\|S | `208741763` | Testing |
| Tapping Skill Test | Xbox One | `875079140` | Testing |
| Titanfall | Xbox One, Xbox Series X\|S | `1292135256` | Testing |
| Transformers: Rise of the Dark Spark | Xbox One, Xbox Series X\|S | `1529724759` | Testing |
| UFC® 4 | Xbox One, Xbox Series X\|S | `465979625` | Testing |
| Zoo Tycoon | Xbox One, Xbox Series X\|S | `711381438` | Testing |
| Zoo Tycoon: Ultimate Animal Collection | Windows PC, Xbox One, Xbox Series X\|S | `33936946` | Testing |
| ブリッジコンストラクター：ウォーキング・デッド | Xbox One, Xbox Series X\|S | `1787967632` | Supported |

## Every newly Supported title since v1.0.6

These **43** titles are newly Supported compared with v1.0.6, including promotions of existing Testing entries. The server exposes every Supported title listed here.

| Title | Platform | Title ID | Status |
|---|---|---|---|
| 8-bit Adventure Anthology: Volume I | Xbox One, Xbox Series X\|S | `506071367` | Supported |
| Cuphead | Windows PC, Xbox One, Xbox Series X\|S | `623325843` | Supported |
| Don't Starve Together: Console Edition | Xbox One, Xbox Series X\|S | `1033937373` | Supported |
| Don't Starve: Giant Edition | Windows PC, Xbox One, Xbox Series X\|S | `2061746629` | Supported |
| Dungeon of the Endless | Xbox One, Xbox Series X\|S | `429018095` | Supported |
| Dungeon of the ENDLESS™ | Windows PC | `1959656817` | Supported |
| EA SPORTS FC™ 24 Xbox One | Xbox One, Xbox Series X\|S | `2098741842` | Supported |
| EA SPORTS FC™ 24 Xbox Series X/S | Xbox Series X\|S | `1852502655` | Supported |
| EA SPORTS FC™ 25 Xbox One | Xbox One, Xbox Series X\|S | `1882096658` | Supported |
| EA SPORTS FC™ 25 Xbox Series X/S | Xbox Series X\|S | `1653663875` | Supported |
| EA SPORTS FC™ 26 Xbox One | Xbox One, Xbox Series X\|S | `1852068331` | Supported |
| EA SPORTS FC™ 26 Xbox Series X/S | Xbox Series X\|S | `1909324253` | Supported |
| EA SPORTS™ FIFA 16 | Xbox One, Xbox Series X\|S | `2052426459` | Supported |
| EA SPORTS™ FIFA 17 | Xbox One, Xbox Series X\|S | `69094388` | Supported |
| EA SPORTS™ FIFA 20 | Xbox One, Xbox Series X\|S | `196631666` | Supported |
| EA SPORTS™ FIFA 23 Standard Edition Xbox One | Xbox One, Xbox Series X\|S | `1994003829` | Supported |
| EA SPORTS™ FIFA 23 Standard Edition Xbox Series X/S | Xbox Series X\|S | `2071676002` | Supported |
| Enter The Gungeon | Windows PC, Xbox One, Xbox Series X\|S | `401515813` | Supported |
| FIFA 14 | Xbox One, Xbox Series X\|S | `1813362885` | Supported |
| FIFA 18 | Xbox One, Xbox Series X\|S | `433376346` | Supported |
| FIFA 21 Xbox One | Xbox One, Xbox Series X\|S | `1761289795` | Supported |
| FIFA 21 Xbox Series X/S | Xbox Series X\|S | `2137885631` | Supported |
| FIFA 22 Xbox One | Xbox One, Xbox Series X\|S | `1808064759` | Supported |
| FIFA 22 Xbox Series X/S | Xbox Series X\|S | `1847979820` | Supported |
| FINAL FANTASY TYPE-0 HD | Xbox One, Xbox Series X\|S | `1079518187` | Supported |
| FINAL FANTASY TYPE-0 HD (Asian version) | Xbox One, Xbox Series X\|S | `708445180` | Supported |
| FINAL FANTASY TYPE-0™ HD | Xbox One, Xbox Series X\|S | `585022010` | Supported |
| FINAL FANTASY XV MULTIPLAYER: COMRADES | Xbox One, Xbox Series X\|S | `136671143` | Supported |
| FINAL FANTASY XVI | Windows PC, Xbox Series X\|S | `1663711933` | Supported |
| FINAL FANTASY 零式 HD | Xbox One, Xbox Series X\|S | `1342420348` | Supported |
| Just Cause 3 | Xbox One, Xbox Series X\|S | `1175562227` | Supported |
| Mega Man™ Legacy Collection | Xbox One, Xbox Series X\|S | `1535750831` | Supported |
| OCCULTIC;NINE | Xbox One, Xbox Series X\|S | `500760956` | Supported |
| Ori and the Blind Forest | Xbox One, Xbox Series X\|S | `264259050` | Supported |
| Party Hard | Xbox One, Xbox Series X\|S | `916339986` | Supported |
| Project Cars | Xbox One, Xbox Series X\|S | `1907381044` | Supported |
| Project CARS 2 | Xbox One, Xbox Series X\|S | `200732952` | Supported |
| Project CARS 3 | Xbox One, Xbox Series X\|S | `572054432` | Supported |
| Roblox - Xbox | Xbox One, Xbox Series X\|S | `342226876` | Supported |
| Stardew Valley | Xbox One, Xbox Series X\|S | `306230746` | Supported |
| The Witcher 3: Wild Hunt | Xbox One, Xbox Series X\|S | `1799887933` | Supported |
| The Witcher 3: Wild Hunt – Complete Edition | Xbox One, Xbox Series X\|S | `530462567` | Supported |
| ブリッジコンストラクター：ウォーキング・デッド | Xbox One, Xbox Series X\|S | `1787967632` | Supported |

## Existing title blocks updated

Recipe, metadata or support-classification changes are included for these existing titles. This compares parsed blocks rather than JSON formatting.

| Title | Platform | Title ID | Status |
|---|---|---|---|
| 7 Days to Die | Xbox One, Xbox Series X\|S | `60633334` | Testing |
| 7 Days to Die (Game Preview) | Windows PC | `1745806870` | Testing |
| 8-bit Adventure Anthology: Volume I | Xbox One, Xbox Series X\|S | `506071367` | Supported |
| A KING'S TALE: FINAL FANTASY XV | Xbox One, Xbox Series X\|S | `88086791` | Testing |
| ABZU | Windows PC | `2143725369` | Testing |
| ABZU | Xbox One, Xbox Series X\|S | `949699803` | Testing |
| Axiom Verge | Xbox One, Xbox Series X\|S | `55549300` | Coming Soon |
| Burnout Paradise Remastered | Xbox One, Xbox Series X\|S | `1037514907` | Testing |
| Call of Duty: Black Ops III | Xbox One, Xbox Series X\|S | `80579825` | Testing |
| Call of Duty®: Advanced Warfare | Xbox One, Xbox Series X\|S | `875417719` | Testing |
| Call of Duty®: Ghosts | Xbox One, Xbox Series X\|S | `572802557` | Supported |
| Call of Duty®: Modern Warfare® 2 Campaign Remastered | Xbox One, Xbox Series X\|S | `59903186` | Testing |
| Call of Duty®: WWII | Xbox One, Xbox Series X\|S | `545844082` | Testing |
| CHAOS;CHILD | Xbox One, Xbox Series X\|S | `1835818566` | Testing |
| Cuphead | Windows PC, Xbox One, Xbox Series X\|S | `623325843` | Supported |
| DARK SOULS: REMASTERED | Xbox One, Xbox Series X\|S | `125226722` | Testing |
| Dishonored® Definitive Edition | Xbox One, Xbox Series X\|S | `1100121502` | Supported |
| Don't Starve Together: Console Edition | Xbox One, Xbox Series X\|S | `1033937373` | Supported |
| Don't Starve: Giant Edition | Windows PC, Xbox One, Xbox Series X\|S | `2061746629` | Supported |
| Dungeon of the Endless | Xbox One, Xbox Series X\|S | `429018095` | Supported |
| Dungeon of the ENDLESS™ | Windows PC | `1959656817` | Supported |
| EA SPORTS FC™ 24 Xbox One | Xbox One, Xbox Series X\|S | `2098741842` | Supported |
| EA SPORTS FC™ 24 Xbox Series X/S | Xbox Series X\|S | `1852502655` | Supported |
| EA SPORTS FC™ 25 Xbox One | Xbox One, Xbox Series X\|S | `1882096658` | Supported |
| EA SPORTS FC™ 25 Xbox Series X/S | Xbox Series X\|S | `1653663875` | Supported |
| EA SPORTS FC™ 26 Xbox One | Xbox One, Xbox Series X\|S | `1852068331` | Supported |
| EA SPORTS FC™ 26 Xbox Series X/S | Xbox Series X\|S | `1909324253` | Supported |
| EA SPORTS™ FIFA 16 | Xbox One, Xbox Series X\|S | `2052426459` | Supported |
| EA SPORTS™ FIFA 17 | Xbox One, Xbox Series X\|S | `69094388` | Supported |
| EA SPORTS™ FIFA 20 | Xbox One, Xbox Series X\|S | `196631666` | Supported |
| EA SPORTS™ FIFA 23 Standard Edition Xbox One | Xbox One, Xbox Series X\|S | `1994003829` | Supported |
| EA SPORTS™ FIFA 23 Standard Edition Xbox Series X/S | Xbox Series X\|S | `2071676002` | Supported |
| EA SPORTS™ NHL® 18 | Xbox One, Xbox Series X\|S | `134413498` | Testing |
| EA SPORTS™ NHL® 18 | Xbox One, Xbox Series X\|S | `374274123` | Testing |
| Enter The Gungeon | Windows PC, Xbox One, Xbox Series X\|S | `401515813` | Supported |
| FIFA 14 | Xbox One, Xbox Series X\|S | `1813362885` | Supported |
| FIFA 18 | Xbox One, Xbox Series X\|S | `433376346` | Supported |
| FIFA 21 Xbox One | Xbox One, Xbox Series X\|S | `1761289795` | Supported |
| FIFA 21 Xbox Series X/S | Xbox Series X\|S | `2137885631` | Supported |
| FIFA 22 Xbox One | Xbox One, Xbox Series X\|S | `1808064759` | Supported |
| FIFA 22 Xbox Series X/S | Xbox Series X\|S | `1847979820` | Supported |
| Forza Horizon 2 Presents Fast & Furious | Xbox One, Xbox Series X\|S | `1519874468` | Testing |
| Forza Motorsport 5 | Xbox One, Xbox Series X\|S | `2067126551` | Testing |
| Gears of War 4 | Windows PC, Xbox One, Xbox Series X\|S | `552499398` | Testing |
| Gears of War: Ultimate Edition | Xbox One, Xbox Series X\|S | `1475571605` | Testing |
| Gears of War: Ultimate Edition for Windows 10 | Windows PC | `1751973977` | Testing |
| Grand Theft Auto V | Xbox One, Xbox Series X\|S | `1050025307` | Testing |
| Grand Theft Auto V (Xbox One) | Xbox One, Xbox Series X\|S | `972249091` | Testing |
| Halo 5: Guardians | Xbox One, Xbox Series X\|S | `219630713` | Testing |
| Happy Dungeons | Xbox One | `928096970` | Testing |
| Happy Wars | Windows PC | `1761060600` | Testing |
| Just Cause 3 | Xbox One, Xbox Series X\|S | `1175562227` | Supported |
| LEGO Batman 3: Beyond Gotham | Xbox One, Xbox Series X\|S | `101393300` | Testing |
| LEGO Bricktales | Xbox One, Xbox Series X\|S | `2005013689` | Testing |
| LEGO City Undercover | Xbox One, Xbox Series X\|S | `668438210` | Testing |
| LEGO Dimensions | Xbox One, Xbox Series X\|S | `389571069` | Testing |
| LEGO Jurassic World | Xbox One, Xbox Series X\|S | `1029888556` | Testing |
| LEGO Marvel Super Heroes | Xbox One, Xbox Series X\|S | `470244957` | Testing |
| LEGO Marvel Super Heroes 2 | Xbox One, Xbox Series X\|S | `813411968` | Testing |
| LEGO Marvel's Avengers | Xbox One, Xbox Series X\|S | `640703885` | Testing |
| LEGO STAR WARS: The Force Awakens | Xbox One, Xbox Series X\|S | `565406974` | Testing |
| LEGO The Hobbit | Xbox One, Xbox Series X\|S | `1764536463` | Testing |
| LEGO Worlds | Xbox One, Xbox Series X\|S | `867642949` | Testing |
| Madden NFL 15 | Xbox One, Xbox Series X\|S | `2147358433` | Testing |
| Madden NFL 16 | Xbox One, Xbox Series X\|S | `1249526091` | Testing |
| Madden NFL 17 | Xbox One, Xbox Series X\|S | `2014880780` | Testing |
| Madden NFL 25 | Xbox One, Xbox Series X\|S | `142841733` | Testing |
| Mega Man™ Legacy Collection | Xbox One, Xbox Series X\|S | `1535750831` | Supported |
| Metal Gear Solid V: Ground Zeroes | Xbox One, Xbox Series X\|S | `650249872` | Supported |
| Microsoft Bingo | Windows PC, Mobile | `1047964830` | Testing |
| Minecraft: Xbox One Edition | Durango | `345903534` | Testing |
| Mirror's Edge Catalyst | Xbox One, Xbox Series X\|S | `1824138520` | Testing |
| OCCULTIC;NINE | Xbox One, Xbox Series X\|S | `500760956` | Supported |
| Ori and the Blind Forest | Xbox One, Xbox Series X\|S | `264259050` | Supported |
| Party Hard | Xbox One, Xbox Series X\|S | `916339986` | Supported |
| Pixel Piracy | Xbox One, Xbox Series X\|S | `819060983` | Testing |
| Project Cars | Xbox One, Xbox Series X\|S | `1907381044` | Supported |
| Project CARS 2 | Xbox One, Xbox Series X\|S | `200732952` | Supported |
| Project CARS 3 | Xbox One, Xbox Series X\|S | `572054432` | Supported |
| Roblox - Xbox | Xbox One, Xbox Series X\|S | `342226876` | Supported |
| Rocket League® | Xbox One, Xbox Series X\|S | `558797228` | Testing |
| Songbringer | Windows PC | `908546367` | Supported |
| Sonic Forces | Xbox One, Xbox Series X\|S | `129325438` | Testing |
| Stardew Valley | Xbox One, Xbox Series X\|S | `306230746` | Supported |
| State of Decay 2: Juggernaut Edition | Windows PC, Xbox One, Xbox Series X\|S | `1938834516` | Testing |
| Terraria | Xbox One, Xbox Series X\|S | `389597738` | Testing |
| The Crew | Xbox One, Xbox Series X\|S | `1979014977` | Testing |
| The Elder Scrolls Online | Windows PC, Xbox One, Xbox Series X\|S | `1285166951` | Testing |
| The Escapists | Xbox One | `1128640842` | Testing |
| The Escapists 2 | Xbox One | `759485819` | Testing |
| The LEGO Movie Videogame | Xbox One, Xbox Series X\|S | `1755825192` | Testing |
| The Witcher 3: Wild Hunt | Xbox One, Xbox Series X\|S | `1799887933` | Supported |
| The Witcher 3: Wild Hunt – Complete Edition | Xbox One, Xbox Series X\|S | `530462567` | Supported |

## Validation and testing scope

The synchronized catalogue sweep passed for **435 local routes, 17,784 usable recipes and 58,749 strict JSON payloads**, checking title identity without live submissions.

Offline checks cover application initialization and scrolling, queue state and account isolation, catalogue payload structure/title identity, mapping boundaries, stats support, exports, window placement and protected storage. The packaged executable is checked separately for startup and helper extraction.

Catalogue support classifications follow the author's data. Structural validation and accepted collector requests do not independently confirm live achievement/stat credit. Live service behaviour and account-specific availability still require user testing. This release is Windows-only and distributed as a self-contained executable.
