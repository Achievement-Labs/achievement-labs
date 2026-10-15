# Achievement, Steam navigation and stats UI update

- Achievement sorting is in the All achievements / Locked / Unlocked toolbar, next to Open Auto Unlock.
- Global % displays Xbox's `rarity.currentPercentage`: the percentage of Xbox players of the title who unlocked the achievement. It is separate from the connected player's progress. Unknown values display a dash, while a returned zero displays 0%.
- Achievement rows load the returned icon URL, with a fallback symbol. Rarity and icons are retained when available through exports and legacy reads. No global percentage or image URL is invented when Xbox omits it.
- Steam > Auto Unlocker is its own page, with a dedicated queue-status panel. It is separate from the Xbox single-game and Build Queue tabs and highlights its own navigation item.
- The project GitHub action opens https://github.com/ethanwp28/achievement-labs/.
- Each Xbox library row has Copy ID, Open achievements and Open stats. Open stats sets that row's Title ID and opens the stats editor without issuing a write.
- Stats type and support are visible independently of achievement type. Event-stat support reads Stats.json plus a standalone title template; it never infers stat support from achievement recipes. Missing/disabled recipes remain unsupported. Local catalogue reads are read-only, with the embedded author snapshot used when a local Stats.json is absent. Mapped event-stat rows retain account/title checks and validate all payloads before any submission; a successful event submission does not claim the value was verified.
- Settings > Window and appearance > Always open maximized uses the saved startup preference. Enable it and click Save all settings. Remember window size and position can also retain the preferred monitor and restore dimensions.

Offline verification covers rarity boundaries/missing values, Steam/Xbox navigation separation, exactly three library actions, mapped/missing stat support, stale rows, invalid event payloads, catalogue preservation, virtualized scrolling, exports and native screen rendering. Live Xbox rarity/icon availability and actual stat credit require user testing. No new catalogue mappings were added in this update.
