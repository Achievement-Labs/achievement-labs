# Startup, search and completion fixes (2026-11-10)

The release candidate keeps compressed event recipes in the executable and loads
selected titles on demand. Normal startup no longer expands a second full catalog
into Documents. Full exports remain an explicit inspection/editing operation.
Existing custom-path editing and local stat recipes remain supported. No live
Events files, replacement operations or payload hypotheses were changed.

## Measured hot paths

Separate Release-mode processes on the same machine, using
`tools/EventCatalogChecks --profile-catalog`:

| Measurement | Before | After |
| --- | ---: | ---: |
| Support lookup for 576 titles | 4,412 ms | 113 ms |
| Cumulative managed allocation during support lookup | 2,479,616,832 B | 3,782,680 B |
| Process peak working set during support benchmark | 506,249,216 B | 45,064,192 B |
| 100 bundled searches | 253 ms | 3 ms |
| Allocation for those searches | 36,264,000 B | 47,200 B |
| Embedded event ZIP | 10,501,532 B | 1,892,537 B |
| Core assembly | 12,183,552 B | 3,576,832 B |

These are isolated measurements of catalog/search code. They are not measurements
of the entire signed-in Avalonia application's working set. Allocation is
cumulative bytes allocated, not simultaneous RAM consumption. Timings vary by
machine and caches.

Support badges previously decompressed and parsed every title's recipes. They
now use the existing compact published metadata; actual selected-title recipe
validation still uses the consumer. Bundled metadata is parsed once, rather than
once per local search. Library names cache their normalized search keys, and
merging locally loaded Xbox 360 titles uses a set instead of nested scans.

The packer now includes only the index, Stats.json and each indexed title's
mapping/template pair. The unused inventory and research JSON are excluded from
the executable's recipe archive. All 1,154 retained files were verified
byte-for-byte against the chosen source; the archive does not rewrite payloads.

Hosted search shows local results immediately, uses a dedicated pooled public
HTTP client and cancels an obsolete search when the query changes. Automatic
API totals refresh is bounded to one batch of 100 missing IDs per refresh and
remembers successful missing-ID responses for a day. Known shared totals are not
re-requested. Failed HTTP responses do not create missing-ID cache entries.
Unknown counts stay unknown; the user-initiated scan remains available.

## Mythic undercount

The response model previously dropped `achievementSummary.mythicsUnlocked` and
per-title `isMythic`, `baseGameCount`, `baseGameCompleted`. These fields are now
nullable, so an absent marker is distinct from an explicit false marker.

The dashboard uses the Xbox account total when returned, with the number in the
loaded title rows shown separately. A complete returned title history is not
assumed: an account summary can exceed the number of Mythic rows in that response.
An explicit per-title marker works when totalAchievements is missing; explicit
false takes precedence over older local inferences. Local verified base/DLC
progress remains a fallback for responses without the new fields.

The Mythics library filter makes counted loaded titles inspectable. Completion
unverified identifies loaded titles lacking aggregate data and an explicit Xbox
base-completion marker. Local Xbox 360 titles participate in the loaded-library
summary, deduplicated by exact title ID. Windows 8/GFWL editions remain distinct;
modern/legacy aliases are not merged by game name.

## Package-size options

The vendored Steam source is about 100 KB. The app/helper compile its API code;
the whole SAM repository is not shipped as loose content. Removing that code
would lose functionality for little size benefit.

The larger packaging option for a later change is to share the x64 .NET runtime
with the Steam helper, or offer a framework-dependent package. The existing
self-contained helpers trade download size for no runtime prerequisite. The x86
GFWL helper still needs an x86 runtime. This change does not alter helper launch
or deployment behavior.

Fetching recipes per title could further shrink the initial download, but needs
a versioned server API, checksum-validated per-title cache and an offline policy.
The current small compressed bundle retains offline use without that dependency.
The public metadata API supplies definition totals/search; account-specific
unlocked counts and completion markers come directly from the Xbox session.
Third-party tracker logins are not required for these fixes.

## Validation

- Windows desktop and enhancement checks build successfully.
- Completion/queue regressions pass, including missing totals, explicit server
  markers, absent fields, account summary and inspectable Mythic filtering.
- Runtime packer allowlist, source changes, edits, additions/removals, path checks,
  missing-template rejection and deterministic output checks pass.
- Compiled resource verification passes for all 1,154 runtime files.
- Actual offline consumer renders 576 titles, 25,830 achievement mappings and
  177,860 payloads with zero failures.
- No telemetry, achievement unlocks, release publication or API deployment.
