# Achievement Labs v1.1.0

Draft Windows feature release. The platform-library PR must be reviewed and merged
before the release is published. Mac and Linux packages remain on their separate
preview track.

## Platform libraries and achievements

- Browse owned EA, Epic, GOG and Ubisoft libraries, including uninstalled titles.
- Use separate Library and Achievements pages, with game selection, search,
  filtering, sorting and account-aware caches.
- Attach the account already signed in to a supported Windows launcher, or use
  browser sign-in. Account attachment verifies the provider identity and supports
  cancellation; it does not launch games or unlock achievements.
- Load EA, Epic and GOG achievement definitions and available player progress on
  demand. Ubisoft definitions and available native progress are read without
  modifying the launcher cache. Missing or unavailable progress remains Unknown.
- Improve Steam account-library browsing, artwork, achievement presentation and
  separate library/achievement navigation.
- Use the EA Play platform label and open EA Play or GOG Galaxy from new home
  tiles. The six-tile home dashboard adapts to narrow windows without overlap.

The four additional platform adapters do not add online achievement writes or
standalone spoofing. Provider availability and account access vary; offline tests
are not a promise that every title or launcher version works with a live account.

## Event catalog

- Includes the merged 557-title catalog: 351 Supported, 89 Partially Supported,
  and 117 Testing.
- Loads recipes and templates from per-title folders using a small index.
- Keeps compatibility with legacy flat catalogs and preserves existing request
  counts, replacement values and standalone templates.
- Keeps research and diagnostics outside runtime configuration. No automatic
  migration or modification of F:\Events is performed.

## Updating

Install the updated Windows executable to acquire the new platform features and
per-title loader. Existing installations do not acquire code changes merely from
a repository merge or licensing-database sync. Existing settings and account data
remain outside the executable. The published update.json must match the final
Windows executable's numeric version, size and SHA-256.

Publish the nested event catalog as Events-Indexed.zip. Keep a compatible flat
Events.zip available for older clients; do not replace their legacy asset with a
nested catalog. Mac/Linux preview packages are not Windows update assets.

## Validation

Release preparation uses synthetic provider responses, fictional accounts, local
catalog rendering and native UI previews. No live unlocks, telemetry submissions,
game launches or production sign-ins are performed as part of release checks.
