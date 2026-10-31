# Account library integration



`Platforms/AccountLibraryClient.cs` owns the account-library requests and metadata normalization. `PlatformBrowserLogin.cs` handles an explicitly opened, isolated browser sign-in window. `ConnectedPlatformAccount` protects saved sessions with Windows DPAPI. Media requests use a separate client without account credentials.



Library membership is fetched before installation enrichment. Pagination must finish before the account-specific cache is replaced; repeated cursors fail rather than publishing a partial result. Metadata failures may leave individual covers unavailable, but cannot turn an installation catalog into the account library. Installation merging preserves account boundaries for achievement state.



The adapters implement original C# clients against protocol behavior described by these primary implementation references:



- Epic library cursors, catalog metadata, authorization-code/refresh exchanges: [Legendary API adapter](https://github.com/legendary-gl/legendary/blob/master/legendary/api/egs.py).

- GOG authorization-code/refresh exchanges: [Heroic gogdl authorization manager](https://github.com/Heroic-Games-Launcher/heroic-gogdl/blob/main/gogdl/auth.py).

- GOG releases pagination and GamesDB artwork templates: [Heroic GOG library manager](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/main/src/backend/storeManagers/gog/library.ts).

- EA web sign-in and consolidated entitlements: [Galaxy Origin backend](https://github.com/FriendsOfGalaxy/galaxy-integration-origin/blob/master/src/backend.py) and [sign-in flow](https://github.com/FriendsOfGalaxy/galaxy-integration-origin/blob/master/src/plugin.py).

- Ubisoft web sign-in session fields and owned-games query: [Galaxy Ubisoft backend](https://github.com/FriendsOfGalaxy/galaxy-integration-uplay/blob/master/src/backend.py) and [sign-in parameters](https://github.com/FriendsOfGalaxy/galaxy-integration-uplay/blob/master/src/consts.py).

- Steam owned games: [Valve IPlayerService reference](https://partner.steamgames.com/doc/webapi/IPlayerService).



These launcher/web protocols can change independently of the application. They are not represented as an official cross-platform SDK contract. Automated validation uses fabricated accounts and a fake HTTP handler; it does not prove that a provider currently accepts a real user's login. No production credentials are checked into the repository, supplied to test handlers, or included in reports.



`AccountLibraryChecks` exercises uninstalled ownership, multiple pages, DLC filtering, other-platform filtering, repeated cursors, foreign-host rejection, artwork URL extraction, and PNG/JPEG/WebP decoding. Native render checks stress long titles and horizontal action bounds at 1440/1120/1000/900, with separate Steam/Xbox regressions. Authentication windows and real platform launches are excluded from those automated runs.



## Launcher session attachment

`LauncherSession` orchestrates explicit attachment. `RunningLauncherSession` uses query/read handles to private pages of EpicGamesLauncher, EADesktop or upc, owned by the current Windows user/session. Only candidate tokens reach provider account verification; no process writes, injection, dumps or debug privileges are used. Multiple verified accounts fail closed. Refresh reattaches and rejects account switching until an explicit attach action is taken.

GOG continues to use its registry refresh session with `without_new_session=1`. EA now uses the current Juno GraphQL identity, paginated ownedGameProducts and batched metadata queries. The legacy Origin catalog is no longer the active library route.

`UbisoftOwnershipClient` implements the version/auth/open/initialize subset of the launcher's TLS Demux ownership protocol. It never sends product-started, activation, consumption, game-token, achievement or presence messages. Scoped sessions are obtained only from Ubisoft. Production TLS validation remains enabled. Frames and fields are bounded; incomplete responses are rejected. Only owned playable base products enter the library, with localized title/cover metadata from their configuration. Keys and licenses in unrelated wire fields are never rendered or logged.

Primary references: [Ubisoft ownership wire definitions](https://github.com/Starkka15/Optima/blob/main/proto/ownership.proto), [Demux framing and ownership reads](https://github.com/Starkka15/Optima/blob/main/src/demux.rs), [Ubisoft session scoping](https://github.com/Starkka15/Optima/blob/main/src/auth.rs), [current EA GraphQL identity/library queries](https://github.com/lutris/lutris/blob/master/lutris/services/ea_app.py), [Galaxy registry layout](https://github.com/TCNOco/TcNo-Acc-Switcher/blob/master/Platforms.json).

`--launcher-session-check PLATFORM REPORT` performs an explicit live read without persisting sessions, recording only aggregate counts and sample image-download results. Offline checks never scan running launchers or use real credentials.

## Achievement readers and Ubisoft follow-up (2026-10-29)

Opening a game now starts an account-scoped achievement read rather than displaying only pre-existing snapshot rows. EA uses Juno's achievements query; Epic reads catalog definitions and player profile or sandbox states; GOG uses the paginated gameplay API. Missing player states stay Unknown. The UI supports cancellation, loading/error/empty states, retry, cached results and a game picker independent of library search.

Ubisoft definition ZIPs provide localized names, descriptions and icons. The separate read-only ach_frontend adapter authenticates and reads the account's PC achievement blob; it never sends write requests. Definitions remain available without verified player progress. The ownership client itself continues to send no achievement messages.

The initial Ubisoft native attachment and ownership request succeeded (65 owned games, 64 cover URLs). Testing resumed after the user confirmed Connect was open and signed in. Its currently exposed candidate returns HTTP 401; native achievement progress has not passed live validation. Preserve Unknown states and retry with a provider-valid connection. Do not describe Ubisoft continuity or native progress checks as passed.
