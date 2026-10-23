# Offline event pack import

All three apps have **Import event pack JSON…** in Settings, beside event sync
(in **Sync** on Android and iOS). Save `EventPack.json`, transfer it to the
device, and select it using the system file picker. Import works without GitHub
or an Xbox sign-in. Pause Auto Unlock first. An import never starts a queue.

An event pack includes standalone template strings and separate achievement
replacement mappings. A single `Events/<titleId>.json` or `Data.json` alone
cannot provide both. Existing bundled titles, edition restrictions and previously
imported/synced titles are retained. Only new titles are added, marked Testing.
Zero added titles means the pack did not contain any new usable games. This
fallback does not replace mappings or repair an existing game's recipes.

New entries are checked for title/service identity, replacement targets, payload
validity and size limits before saving. Invalid imports retain the old cache.
Validation checks structure; it does not prove an achievement will unlock or
authenticate the pack's author. Choose a pack from a source you trust.

The official event workflow publishes a downloadable pack at:
https://raw.githubusercontent.com/Achievement-Labs/achievement-labs/mobile-event-feed/EventPack.json
after its first successful run. Keep a local copy for repository outages.

Maintainers can also convert a saved official `Events.zip` locally:

```sh
python tools/mobile/export_event_pack.py Events.zip EventPack.json
```

The converter preserves source files, rejects unsafe archive paths and duplicate
JSON keys, and exports only supported ordinary replacement recipes. Its JSON
uses the existing public feed schema (`schemaVersion: 1`, `upstreamRelease`,
`archiveSHA256`, `generatedAt`, `titles`); each title contains `template` and
`recipes.recipes`. The SHA-256 field describes the source archive, not a signature.
The 32 MB file limit applies on all platforms. Windows' existing Events folder
selection also remains available for a complete extracted catalogue.
