# Achievement Labs v1.3.4

- Faster startup through lazy event support metadata and a smaller runtime event bundle.
- Local Xbox title search across 14,556 catalog entries, with achievement/stat types and platform filters including Windows 8, GFWL and explicit mobile platforms. Search no longer probes Xbox or DBox per result.
- Corrected Mythic/base-game completion reporting using Xbox summary and base-game completion fields.
- Reuse private per-account library snapshots, preserve cached totals and honor shared Xbox read cooldowns. Manual Refresh still requests current data.
- Removed automatic unlock-method scans. Catalog metadata supplies known achievement/stat types.
- Refresh app-managed Events folders on bundle upgrades while preserving custom paths and local changes according to the migration rules.
- Remove obsolete title inventories and bundled Ubisoft spools; choose an external spool folder when needed. Organize Steam helper sources and embedded artwork under vendor/Assets.

The title catalog is a compact snapshot. Account progress still comes from Xbox; this does not remove Xbox rate limits. Unknown platform details stay unknown. No personal account database or source master database is shipped.

Windows x64, self-contained single executable. Event recipes remain bundled. This release does not change achievement payloads or unlock operations.
