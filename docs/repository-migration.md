# Official repository migration

Windows, Android and iOS now use `Achievement-Labs/achievement-labs` for event updates and application release discovery. Windows also reads shared achievement totals, DLC packs, hubs and review links there.

Windows reads the official stable release metadata and downloads its SHA-256-verified `Events.zip`. It retains the existing 128 MiB catalogue limit, ZIP path/expansion checks and digest-based caches. The previous repository URL redirects to the official repository, so old builds reject the returned archive address; the updated build expects the official address directly. Android and iOS read the validated `mobile-event-feed/catalogue.json` branch in the official repository. The mobile-feed workflow verifies the official release's `Events.zip` checksum and preserves recipe operation order before publishing new-title additions. Existing bundled mappings are retained.

The Android and iOS sources and their checks were brought from the previously released fork at `c113b920492b34494d3b928575b34c08f837b227` so all three platforms can be maintained and released here. The next mobile build versions are Android 0.6.2 (10) and iOS 0.12.4 (22). The desktop migration changes repository locations without replacing its existing UI or event recipes.

Before publishing mobile updates, initialize the official mobile event feed and configure the same four `LABS_ANDROID_*` signing secrets used by the previous Android releases. A different signing key cannot update the installed app. PR checks do not require the signing secrets; manually dispatched Android packaging requires all four and fails if any is missing. The iOS artifact remains unsigned for user signing.

Existing installed apps with the old fork URL need this application update once to switch locations. Changing a repository URL in source does not rewrite installed binaries. Each future release should link its PR in this repository.

All three apps now also offer a local event pack JSON import in Settings, independent of the repository location. See [offline import instructions](event-pack-import.md). It adds validated new titles without replacing bundled mappings or starting queues. The event-feed workflow publishes `EventPack.json` alongside the automatic feed.

The official public source was missing its private Xbox 360 CON-signing dependency and did not compile. A key-free fallback now reports signing unavailable before opening a profile for writing; installations supplying the private source retain that implementation. Offline checks also use a temporary saved-queue path instead of the user's queue.
