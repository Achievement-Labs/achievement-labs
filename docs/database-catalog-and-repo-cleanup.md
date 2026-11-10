# Database-derived Xbox catalog and source cleanup

The author's SQLite database is the metadata source. The desktop executable
embeds `catalog/xbox-title-catalog.json.gz`, a metadata-only snapshot exported
read-only from CatalogTitle and FilteredTitle. It does not open the author's F:
drive database on end-user machines, ship the requirement-detail tables, or
require an additional native SQLite runtime. The current snapshot has 14,556
distinct title IDs and is 374,581 bytes compressed.

Refresh the snapshot explicitly after updating the master database:

```powershell
python tools/export_xbox_catalog.py --database "F:/Xbox Stuff/Xbox Stuff/Tools/XboxTitles.db"
python tools/export_xbox_catalog.py --database "F:/Xbox Stuff/Xbox Stuff/Tools/XboxTitles.db" --check
```

The exporter reads a consistent SQLite transaction, closes the source connection,
selects only runtime metadata fields, validates IDs/types/counts, and atomically
replaces its output. It never updates the master database. A packaged snapshot
changes only when explicitly regenerated and included in a new app build.

## Runtime use

Both main title search and diagnostics catalog search are local. Main search no
longer performs spelling-variant DBox requests or per-result DBox/Xbox definition
checks. The full-total column CatalogTitle.TotalAchievements supplies definition totals; FilteredTitle.AchievementCount is not used as a denominator because it can be a rule-covered subset (for example, Forza Motorsport 7 has 62 versus 99). Known definition totals can fill missing library totals; fresh Xbox
counts and existing confirmed totals retain precedence. A count below the user's
earned count is rejected rather than producing a false completion percentage.

Database achievement-type labels fill previously unclassified support metadata.
They do not turn an absent event recipe into an available recipe. Opening a game
still obtains the account's live achievement response before actions. Database
stat-type hints fill unknown modes; existing concrete stat definitions/recipes
are preserved. No event mappings, supported IDs or rendered payloads changed.

## Platform identity

Source-version 1 identifies legacy achievement service records, not event-based
versus title-based achievements. Legacy PC records are separated as Windows 8;
the app's existing exact GFWL IDs/shared-list overrides and legacy PC title-ID
rules distinguish GFWL from console counterparts. Shared verified lists remain
filterable as both GFWL and Xbox 360. Modern PC entries remain PC.

Explicit iOS, Android, Web, Windows Phone, Apple TV, Kindle Fire, Gear VR and
Nintendo Switch values retain their identities. Generic Mobile stays Mobile;
missing devices stay Unknown. No platform is inferred from a title name. The
current database snapshot has no explicit Web rows: a Web filter is supported,
but unknown records are not invented as Web games. Separate title IDs are never
merged just because names match. Known Windows 8 aliases retain the existing
consumer routing and IDs.

## Repository layout

- Steam API wrapper/KeyValue build inputs moved to vendor/SteamAchievementManager.
  Both original license files remain unchanged. It is about 100 KB of source;
  the separate upstream GUI application is not bundled.
- Xbox/Steam/Epic/Ubisoft logos moved from the repository root into desktop Assets.
  EA/GOG logos were already there. Assets are reproducible build inputs embedded
  in the executable; moving/deleting source assets alone does not reduce decoded
  image memory. Build source files must exist somewhere to embed their bytes.
- Three obsolete XboxTitleIDs.json copies were removed, roughly 146 MB from the
  working tree. They had already been excluded from the runtime event bundle.
  Catalog-generation/commerce export tools now read the compact snapshot.
- The 35 bundled Ubisoft spools (about 25 KB) were removed. The spool installer
  remains available for an explicitly chosen external spool library; it does
  not install anything automatically. The chosen source folder is remembered.

Byte-identical originals were archived outside the worktree under the local
outputs/repo-catalog-cleanup-20261110 directory before moves/removals. Existing
Git history is retained. Source cleanup primarily reduces repository clutter;
local metadata lookups and eliminated requests provide the runtime benefit.

## Checks

```powershell
python -m unittest tools.test_export_xbox_catalog -v
dotnet run --project tests/AchievementLabs.Enhancements.Tests -- --xbox-title-catalog
dotnet run --project tests/AchievementLabs.Enhancements.Tests -- --title-search
dotnet run --project tests/AchievementLabs.Enhancements.Tests -- --library-cache
dotnet run --project tests/AchievementLabs.Enhancements.Tests -- --queue-tools
```

Fixtures cover read-only export and connection disposal, field allowlisting,
union/determinism, exact editions, Windows 8/GFWL, separate mobile/Web filters,
local main search and external spool enumeration. All HTTP in account/cache
checks is synthetic. No telemetry or live achievement/stat writes are performed.
