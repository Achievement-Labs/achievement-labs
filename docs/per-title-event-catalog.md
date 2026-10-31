# Per-title event catalogs

The repository maintains `Events/` and the matching bundled source at
`src/AchievementLabs.App/Events/` in this layout:

```text
Events/
  index.json
  Stats.json
  1579532320/
    Data.json
    1579532320.json
```

`index.json` contains numeric title keys and runtime summaries (the title block
without `Achievements`), followed by `SupportedTitleIDs` as its final property.
Each title's `Data.json` contains exactly its numeric title block followed by
`SupportedTitleIDs: ["<titleId>"]`. Its standalone template retains the numeric
filename. Index summaries and title runtime fields must agree. All title blocks,
including disabled and unlisted titles, remain in the index.

The app reads the index to discover titles. It loads and validates the selected
title's mappings and template when needed, caching at most one parsed title per
storage instance. A missing or invalid unrelated title file does not prevent a
valid title from loading. Per-title mapping/template edits invalidate cached
achievement validation using file timestamps and lengths; status changes must
update both the index and title file. Supported IDs and index summaries refresh
when the index changes.

Legacy flat catalogs (`Data.json` plus `<titleId>.json`) and legacy embedded
catalogs remain readable. An explicit indexed catalog takes precedence; malformed
data is reported rather than silently falling back to a different catalog.
`F:\Events` is not migrated automatically.

## Migration and authoring

Create a new snapshot and a complete backup before replacing any catalog:

```powershell
python tools/event_catalog.py split Events C:/staging/events-indexed --backup-dir F:/UWPDumps/events-original
```

The tool never edits its inputs and refuses an existing output or backup. It
keeps replacement values, request order/count and templates intact, removes
research fields from runtime configuration, and preserves originals in the
external backup. Named legacy requests are renamed to contiguous `Request1..N`
without changing their operations. Existing stat recipes move into the separate
`Stats.json` store; conflicting stat recipes fail migration.

The runtime schema guard rejects title research fields, scalar achievement/request
housekeeping, unsupported operations and malformed requests. It permits existing
numeric `Replacement` values and empty replacement strings because the actual
consumer renders these through `JToken.ToString()`; these are legitimate operation
values. `PartiallySupported` remains a consumer-supported runtime status.
Typed telemetry and metadata *inside replacement payloads* are retained.

Reconciliation accepts flat and indexed sources, in increasing priority:

```powershell
tools/Reconcile-EventCatalog.ps1 -Sources Events,F:/Events -OutputDirectory C:/staging/events-merged -BackupDirectory F:/UWPDumps/events-reconcile
```

Backups and research stay under `F:\UWPDumps`, outside runtime catalogs. Every
per-title `Data.json` and index must end in `SupportedTitleIDs`; generated JSON
uses CRLF. Keep both repository catalog copies synchronized. Run the actual
offline consumer checks before publishing; schema validation alone is insufficient.

## Distribution and compatibility

Package only runtime files with the allowlisted archive builder:

```powershell
python tools/event_catalog.py archive Events C:/staging/Events-Indexed.zip
```

Updated desktop clients and the mobile-feed exporter prefer the official stable
release's `Events-Indexed.zip`, falling back to `Events.zip` when the indexed asset
is absent. Both layouts retain checksum verification, path checks and bounded
archive/file sizes. Public additions keep their existing conservative operation
limits and never replace author-maintained titles.

Keep the old flat `Events.zip` available for released clients; do not replace it
with nested files. A legacy export for catalogs that still fit old clients' limits
can be generated using `python tools/event_catalog.py flatten SOURCE NEW_OUTPUT`.
The new folder layout does not remove legacy clients' 128 MiB catalog limit or
their 32 MiB compressed archive limit. Large new titles should ship in updated app
builds or the indexed asset. Mobile app source is maintained separately; this
repository's exporter continues producing the existing mobile feed/pack schema.

The format splits storage; it does not deduplicate repeated requests or reduce
their event count. Those payload semantics remain unchanged.

## Offline checks

```powershell
python -m unittest discover -s tools -p test_event_catalog.py
python -m unittest discover -s tools/mobile -p test_publish_events.py
dotnet run --project tools/EventCatalogChecks
dotnet run --project tools/EventCatalogChecks -- ORIGINAL_FLAT_FOLDER Events
```

The optional full comparison verifies all supported IDs, achievement IDs, ordered
request groups/operations and every rendered achievement. Synthetic identities
and fixture HTTP responses are used; no telemetry is submitted.
