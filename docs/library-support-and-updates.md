# Xbox library support and event updates

The Xbox library shows a support badge per title: Supported, Partial / testing, Unsupported, Not checked, or Legacy. Its tooltip explains the metadata or mapping behind the badge. Support labels describe available unlock methods and catalog mappings; they do not assert that an individual live unlock was verified.

## Checking support

Automatically check game support starts read-only achievement metadata checks after account attachment or a library refresh. Check support starts a manual pass, and Stop support check cancels it while retaining confirmed results. Requests are sequential and spaced apart. Account changes discard in-flight results; authorization failures, timeouts and rate limits stop or delay further requests. Empty, malformed or incomplete responses stay unconfirmed.

Export confirmed support produces a versioned game-method catalog for review. The app also reads an optional shared catalog from the official repository. Its bundled seed is empty. These catalogs and local caches remain separate from Events runtime configuration.

## Event updates

Published event archives can refresh existing cached recipes and support status as well as add titles. Version 2 event packs can update or disable mappings. Legacy version 1 packs remain additive. Disabled overrides cannot fall back to bundled recipes or the licensing service.

Externally supplied local catalogs and protected edition mappings retain priority. Updates preserve the indexed per-title format, validate runtime schema, and render every imported achievement through the ordinary replacement consumer before atomically replacing the cache. Invalid updates retain the last valid cache. Live event source files are not rewritten.

Catalog refreshes wait for active actions/queues, and actions are disabled while an update is running. The library badge and selected achievement mapping refresh afterward.

## Game Session

Open achievements loads the current session title without restarting automatic spoofing. Shared totals and completion metadata load after the initial account/library attachment, with account-generation checks.

## Scope and verification

This change preserves the existing overlay and Xbox 360 workflows. It adds no Windows 8 unlocker, token capture, game launcher, request preparation or submission UI, and makes no version or release-feed changes.

Offline checks cover support classification and exports, indexed mapping counts, archive verification, recipe/status refresh, disabled overrides, restart, payload rendering and rollback. Synthetic native previews cover library badges and the Game Session shortcut alongside existing overlay and Xbox 360 layouts. Live unlocks, telemetry and console synchronization are outside these checks.
