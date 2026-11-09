# Official event catalog

Desktop builds embed every JSON file under `src/AchievementLabs.App/Events`. Merely
committing a file to GitHub does not update an installed app. Publish a stable
release with the matching `Events-Indexed.zip` to distribute catalog changes.

The embedded catalog is the offline baseline. Desktop ignores the configured
local Events folder for normal achievement actions and library support. That
folder remains available to local editing tools. No files in it are migrated,
deleted, or changed by this feature.

## Published classifications

The title runtime flags are authoritative for both badges and access lists:

| Classification | Runtime flags | Access |
| --- | --- | --- |
| Supported | listed, Enabled is not false, FullySupported true | Mapped achievements available |
| Partially Supported | listed, enabled, FullySupported false, PartiallySupported true | Mapped achievements available |
| Testing | listed, enabled, both support flags false/absent | Mapped achievements available |
| Unsupported | disabled, unlisted, or no official mapping | No official event actions |

Mapping coverage is descriptive. Counts, completion percentage, local metadata,
and individual users' Xbox responses never downgrade a published classification.
An unreadable catalog remains a data/update error, not a new support decision.
Standard title-based games retain their standard unlock method. Legacy games
retain their separate tools. The original-service edition `345903534` retains
its existing dedicated route.

`site/assets/games.json` is a presentation export, not a second authority for
desktop access. Update both repository Events mirrors together and regenerate
the site listing when changing statuses. Dark Souls III, Halo MCC, and Halo 5
are confirmed Supported by the catalog owner.

## Updates and failure handling

Checks run at startup and hourly, with a manual retry in Sync settings. The old
AutoRefreshEvents preference no longer suppresses official catalog checks.
The app only downloads official stable release assets from
`Achievement-Labs/achievement-labs`, verifies the GitHub SHA-256 digest, validates
archive paths, and renders every affected achievement through the actual offline
consumer. Validation never submits telemetry.

Official updates require release v1.3.3 or newer. An older published archive
cannot undo the bundled corrections, and an older release cannot replace a newer
verified snapshot. A complete candidate is validated before replacing the cache
and in-memory snapshot. Any invalid title aborts the entire update; the last
working catalog remains usable. Removed/disabled titles are represented explicitly
so stale bundled recipes cannot reappear through fallback.

Official updates use the Brotli-compressed `official-event-catalog-v2.json.br`. The old mixed
`upstream-event-additions.json` cache is not imported. Explicit custom packs use
`custom-event-packs-v2.json`; they cannot override official titles or claim
official Supported status. Their new titles remain visibly Testing.

## Release verification

Run the event-catalog CI workflow before release. It checks both source and
embedded rendering, exact bundle/source equivalence, status/access consistency,
the three reported titles, atomic update rollback, offline restart, and a complete
release ZIP installed through the real updater. The new full-update check is:

```powershell
dotnet run --project tools/EventCatalogChecks -c Release -- --official-release Events
```

Publish the EXE and catalog asset from the same reviewed commit. Import that same
catalog into the licensing service so older remote clients remain consistent.
Existing installed binaries need the app update to adopt this authority change.
No new always-online API dependency is required; a future API can distribute the
same versioned catalog without changing the classification contract.
