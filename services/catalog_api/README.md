# Public catalog metadata API

First API increment: named event-title lookup, explicit status, literal search,
revision-bound pagination, and batch verified definition totals. Python 3.12
standard library only. WSGI application; no account, licensing, billing, telemetry,
or unlock code. This service does not modify the current licensing API.

## Local development

From the repository root:

```powershell
python -m unittest services.catalog_api.test_api -v
python -m services.catalog_api.build --repo . --output artifacts/catalog-api/catalog.sqlite
python -m services.catalog_api.check_snapshot --repo . --database artifacts/catalog-api/catalog.sqlite
python -m services.catalog_api.app --database artifacts/catalog-api/catalog.sqlite
```

The last command starts a development server on `127.0.0.1:8091`. It is not a
production internet-facing server. None of these commands builds an app release,
modifies Events, contacts Xbox, or changes a running deployment.

## Routes

All endpoints accept GET and HEAD and return UTF-8 JSON. These are **new routes
to deploy**, not URLs currently available on achievementlabs.org:

| Route | Response |
| --- | --- |
| `/api/v2/catalog` | Schema/content/source revisions, scope, counts, platforms |
| `/api/v2/catalog/titles` | Searchable, filtered, paginated title records |
| `/api/v2/catalog/titles/{titleId}` | One complete triage record |
| `/api/v2/catalog/totals?ids=1144039928,219630713` | Verified totals for up to 100 requested IDs, plus missingIds |

Example PowerShell queries against the local development server:

```powershell
$base = 'http://127.0.0.1:8091/api/v2/catalog'
Invoke-RestMethod "$base/titles/1144039928"
Invoke-RestMethod "$base/titles?query=halo&status=Supported&platform=XboxOne&limit=50"
Invoke-RestMethod "$base/totals?ids=1144039928,219630713"
```

Search parameters: `query` (up to 200 characters; literal substring of name,
alias, or ID), `status` (case-insensitive enum), `platform` (case-insensitive
device token), `limit` (1–100, default 50), and `cursor`. The metadata endpoint
lists available platform tokens. Results sort by normalized name, then string
title ID. Repeat the same filters and limit when following `nextCursor`.
Changed catalog data returns 409 `catalog_changed`; restart at the first page.

Totals accept `ids` and optional `platform`. Duplicate IDs are collapsed while
retaining request order. `missingIds` means no verified total for that ID/platform,
not zero achievements or Unsupported. The totals table can include titles outside
the event catalog. Requests with invalid/repeated/unknown parameters return 400;
unknown title records return 404; writes return 405; unavailable snapshots return
a generic 503 with no database paths or backend details.

Every successful response includes `catalogRevision` and `sourceRevision`.
For example, a title lookup has this shape (counts shown are illustrative):

```json
{
  "catalogRevision": "<content SHA-256>",
  "sourceRevision": "<Git commit>",
  "titleId": "1144039928",
  "name": "Halo: The Master Chief Collection",
  "aliases": [],
  "platforms": ["PC"],
  "status": "Supported",
  "eventAvailable": true,
  "availabilityReason": null,
  "mappedAchievementCount": 580,
  "definitionAchievementCount": 700,
  "definitionCheckedAt": "2026-01-01T00:00:00Z",
  "mappingRevision": "<mapping SHA-256>"
}
```

## Authority and data boundaries

The snapshot reads canonical `Events/index.json` and each title's runtime file
and template, public names in `site/assets/games.json`, and verified entries in
`catalog/achievement-totals.json`. It verifies index/title agreement, ordering,
identities, runtime boolean types, and definition counts/check times, reusing the
repository's runtime mapping schema validator during generation. It does
not replace the existing actual-consumer payload/render validation in catalog CI.

The website's status label is deliberately ignored. Runtime flags determine:

- **Supported:** listed, enabled, FullySupported true.
- **PartiallySupported:** listed, enabled, not fully supported, PartiallySupported true.
- **Testing:** listed, enabled, neither support flag true.
- **Unsupported:** explicitly disabled or unlisted in this snapshot.

An active title with a missing template rejects the entire candidate snapshot;
it is never silently relabeled Unsupported. An entry with no mapped achievements
retains its published classification but exposes `eventAvailable=false` and
`availabilityReason="no-mappings"`. A mapping count below a known definition total
never downgrades the classification. Unknown totals are null. Platform-mismatched
definition totals are not attached to title records.

This first title-search scope is the **event catalog**, not every Xbox game.
Absence is not a determination of support for standard title-based or legacy
unlock methods. Definitions/totals outside the event catalog remain queryable in
batches, independently of event membership.

Only whitelisted public metadata reaches the SQLite snapshot. Recipes, templates,
replacement operations, user progress, XUIDs, account tokens, and billing data do
not. Mapping revisions are hashes of the ordered runtime mapping and template;
replacement execution order is significant. Catalog revision hashes the public
projection, including mapping revisions, so recipe changes invalidate pagination
even when achievement counts remain the same.

Snapshot generation validates all source data before creating a temporary SQLite
database, closes it, checks integrity, and atomically replaces the destination.
Invalid imports preserve the previous database. Runtime connections use SQLite
read-only mode and one read transaction per request. No initialization or writes
occur in the request path. Keep the served database outside the source repository.

## Deployment and compatibility

Expose `create_app()` in the existing production WSGI hosting environment with
`CATALOG_DATABASE` set to the metadata snapshot path. Add only the reverse-proxy
route `/api/v2/catalog` and its children to this service; retain all current
`/api/licensing/` routes and the released mobile feed. The snapshot must be mounted
read-only into the serving process. Apply public-endpoint rate limits at the
reverse proxy. No deployment, reverse-proxy changes, or authentication changes
are performed by this PR.

Responses support representation-specific ETags, conditional GET/304, gzip, and
HEAD; successful public data has `max-age=60, must-revalidate`. Error responses
use `no-store`. Public CORS allows cross-origin reads without credentials; ETag is
exposed, with OPTIONS preflight support for If-None-Match. Do not apply this public
caching policy to private account routes.

Generate the snapshot from the same reviewed catalog revision used for bundled
app data and retain the previous snapshot for rollback. On Windows, an active
reader can prevent replacement; a failed replacement leaves the existing file
intact. Retry publication after readers close or switch versioned snapshot paths
with a process restart. Never mutate the served SQLite file in place.

This independent PR reads current main's classifications. Status corrections in
draft PR #51 are not duplicated here: rebuilding after that PR merges publishes
the corrected source flags. Existing apps do not start calling this API until a
subsequent client integration change. App-side loading improvements, hosted
definition collection, account progress, and cross-device sync are later increments.

## Hosted desktop integration (v1.3.3)

Production base URL: https://achievementlabs.org/api/v2/catalog
The desktop Event catalog > Search catalog tab queries this service without
Xbox authorization, cookies, XUIDs, or account progress. Results show published
support classifications and definition totals. A failed/offline request uses the
bundled public metadata snapshot and explicitly labels it offline. Searches show
up to 100 matches; refine the query to see other matches. Library name filtering
continues to work locally on the user's own library.

Library background refresh requests missing definition totals in batches of 100
IDs, validates one catalog revision across batches, and merges verified totals
into the existing bundled/cache fallback. Earned counts remain Xbox-sourced;
missing API IDs never become zero counts or Unsupported. Event recipes/templates
continue to ship in full and update through the verified GitHub event archive.
The API cannot grant or remove access to runtime mappings.

Regenerate bundled search metadata after canonical catalog changes:
`python -m services.catalog_api.export_client --repo .`
CI checks the projection against canonical inputs. The production build helper
checks it too, so stale metadata cannot ship silently.

Hosting uses `serve.py` with Waitress in a separate, restricted container on the
site's private network. Only public code and the read-only SQLite snapshot are
mounted. No licensing database, secret mount or host port is shared. The existing
site reverse proxy adds only `/api/v2/catalog` and its descendants, rate-limited
at 10 requests/second with a burst of 40. Existing licensing routes are unchanged.

To deploy a new snapshot, stage the module source under services/catalog_api,
its validated catalog.sqlite, and deploy.py in a new timestamped server folder.
Run `python3 deploy.py --stage /path/to/new-stage` on the existing host. The helper
pins the already-installed production Python/Waitress image by image ID, saves
the previous nginx configuration, starts the new service, checks its WSGI response,
validates nginx, then reloads it. Each version and its previous proxy config are
retained under /opt/achievement-labs/catalog-api. It rolls back routing on failure.
After deployment, verify the public HTTPS endpoints with the app user agent and
check website/licensing health. Rollback: restore the recorded proxyBackup into
the existing nginx.conf inode, run nginx -t, then reload ual-site; previous service
containers and snapshots are retained for that purpose.

Deploying metadata is an explicit maintenance operation; the service does not
pull arbitrary GitHub branches or mutate the served snapshot in place. Publish a
new validated snapshot whenever canonical labels, titles or verified totals change.
