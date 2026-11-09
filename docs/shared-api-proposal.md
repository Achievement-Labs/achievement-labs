# Shared API proposal: catalog, totals, search, and mobile sync

This is a proposed contract, not a description of deployed endpoints. The live
licensing `/catalog` endpoint currently returns ID lists and combines Partial with
Testing. The desktop authority fix works with a bundled catalog and verified
release updates; it does not depend on deploying this proposal.

## What is actually slow today

The desktop already uses APIs. `XboxApiClient.GetGamesListAsync` reads Xbox title
history, which can include earned and total achievement counts. Shared title
totals are bundled and refreshed from a public JSON catalog. Missing totals are
queried separately from Xbox achievement definitions, including pagination and
legacy/modern fallback.

Two concrete waits exist in `DesktopModel.Library.cs`:

1. `RefreshLibraryAsync` waits for shared unlock methods, shared totals, and DLC
   metadata before starting the Xbox library request. These are sequential shared
   refreshes with five-second HTTP timeouts, under the shared-metadata gate.
2. `FillMissingTotalsAsync` walks titles one at a time. Every title can require
   multiple definition pages and a second endpoint attempt. It also republishes
   the Games array after individual results, triggering library presentation work.

An API that simply proxies those same requests on every click would add a hop.
The gain comes from shared, verified definition data, batch reads, caching, and
showing cached account progress immediately while refreshing it separately.

## Separate public definitions from private progress

| Data | Source/owner | Cache and sync behavior |
| --- | --- | --- |
| Title names, devices, aliases, artwork references | Shared catalog | Public, versioned; same IDs on mobile/desktop. |
| Support classification and availability | Owner-maintained official catalog | Publish together with mapping revision; never derive from device files or completion percentage. |
| Achievement definitions, total, groups/DLC | Verified shared definitions | Reusable across users; include platform/edition and checked time. |
| Earned count and unlocked IDs | The signed-in user's Xbox progress | Private, account-specific; include last verification time. Never inferred from the shared total. |
| Queue selections and settings | User's devices | Sync as intent/configuration; never treat a submitted action as a confirmed unlock. |

## Suggested public API

Example routes, all future additions:

```text
GET /api/v2/catalog                         -> revision, schema, update time
GET /api/v2/catalog/titles?query=halo&platform=xbox&status=supported&cursor=...
GET /api/v2/catalog/titles/1144039928         -> complete triage record
GET /api/v2/catalog/totals?ids=1144039928,219630713
GET /api/v2/catalog/titles/1144039928/achievements?cursor=...
```

One title record should include `titleId`, `name`, `platforms`, `status`,
`eventAvailable`, `mappedAchievementCount`, `definitionAchievementCount`,
`catalogRevision`, `mappingRevision`, and `updatedAt`. Status is one explicit enum:
Supported, PartiallySupported, Testing, Unsupported. Unknown/absent is a lookup
result, not an automatic claim of Unsupported. Missing definitions and service
errors also do not rewrite support classifications.

`mappedAchievementCount` must not be substituted for the total achievement count.
MCC is a concrete example: its declared support remains Supported while mapping
coverage and definition coverage remain separate facts.

Batch totals avoid hundreds of repeated definition scans. Use bounded batches
and pagination; conditional GETs with ETag/If-None-Match; compression; last-known
good local storage; and a bundled baseline for offline startup. Public records
must not contain Xbox authorization, user IDs, account progress, or private
backend material. Publish catalog revisions atomically and retain rollback.

The same title-record endpoint is the support triage tool: search by name or ID,
compare the user's reported catalog revision, and see exact mapping availability.
The app should display its catalog revision, last successful check, and loading
error separately from status. An API query cannot otherwise reveal which catalog
an old installed binary actually loaded.

## Private cross-device API

```text
GET  /api/v2/me/progress?since=...
PUT  /api/v2/me/progress/{titleId}          (versioned, account-bound snapshots)
GET  /api/v2/me/queues?since=...
PUT  /api/v2/me/queues/{queueId}           (versioned queue configuration)
POST /api/v2/me/devices/pair
```

Mobile and desktop need a common app account/device identity and shared versioned
DTOs. Device pairing should grant scoped access to that account. The licensing
identity alone must not be assumed to prove ownership of an arbitrary Xbox XUID.
Do not make unauthenticated progress uploads or store Xbox tokens in the public
catalog. Decide explicitly whether progress refresh remains on devices or a
user-authorized server component is allowed to hold refresh credentials.

For an initial bridge, keep Xbox credentials on devices. Each device reads Xbox,
uploads an authenticated account-bound snapshot, and downloads the other device's
verified state. Use server versions/ETags, idempotent writes, deletion markers,
and explicit conflict handling. Preserve pending local queue edits; do not use
blind last-write-wins for unlock state or signed-in-account changes. Switching
accounts must select a different progress cache and sync scope.

Existing encrypted mobile transfer and LAN pairing can remain as offline options.
Cloud sync replaces the manual transport, not the requirement to verify identity
and handle differing app/schema versions.

## Client experience and rollout

1. Show cached library rows and bundled/shared totals immediately. Mark unknown
   counts with a dash, not a manufactured zero; show when cached progress was
   checked. Refresh metadata independently of the initial library render.
2. Add the public title/status/search and batch-totals endpoints backed by one
   catalog revision. Keep current clients' endpoints compatible. Feed web/mobile
   and desktop from the same maintained records.
3. Share DTOs and client behavior: cache expiration, ETags, cancellation, pagination,
   rate-limit backoff, account separation, and offline/error states.
4. Add opt-in authenticated progress and queue sync, initially with credentials
   retained on the devices. Test account switching, conflicting edits, stale
   devices, offline reconnect, and an old mobile build against the bridge release.

Measure time to first library rows, time until earned/total counts are visible,
definition cache-hit rate, Xbox requests per library, and rate-limit responses.
Measure before deciding whether a server-side account refresh worker is needed.
Do not replace one serial per-title scan with the same serial scan behind an API.

This service would help consistency and synchronization. It does not remove the
need to bundle the complete event catalog, and it will not by itself meaningfully
shrink the EXE while that offline requirement remains in force.
