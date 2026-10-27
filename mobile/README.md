# Achievement Labs — standalone iOS beta

> Experimental mobile event-token test: see [test instructions](../docs/mobile-event-token-test.md). Public fork .15 remains unchanged. Successful token issuance and acceptance by the event collector are separate checks.

Native SwiftUI app for iPhone and iPad, iOS/iPadOS 17 or later. No PC companion or running desktop app is required. This is a separate early mobile beta, not a replacement for the Windows release.

## Current test build — iOS 0.12.10 (28)

This is the unofficial vampirebat fork of the [upstream Achievement Labs project](https://github.com/Achievement-Labs/achievement-labs). App update checks/downloads use [vampirebat releases](https://github.com/thevampirebat/achievement-labs/releases). Event JSON stays upstream. Older installed binaries need one manual corrected install to move their hardcoded app-update feed.

Game Session combines spoofing and Auto Unlock with separate explicit Start actions. Library shortcuts select both controls for the chosen game while preventing active-session retargeting. Old spoof routes and launch preferences resolve to Game Session. Xbox 360 is a separate tab with native exported-profile import, library browsing, artwork when cached, filtering/sorting, green unlocked rows, descriptions, selected achievement/timestamp editing, automatic verified backups, rehash/resign and signed document export. The original export is read-only; console/USB copying remains separate. No exported profile or account token is sent to the editor's WebView network. See [mobile Xbox 360 details](../docs/mobile-xbox360.md).

Before generating the Xcode project, run both `python3 mobile/tools/prepare_catalogue.py` and `python3 mobile/tools/prepare_360.py`. The latter prepares shared offline assets and the user-authorized CON identity from the checksum-pinned public Windows fork build without executing it. Generated binary inputs remain ignored by Git. CI tests synthetic profiles through the real WKWebView/native import/save/export bridge. Console acceptance and server synchronization require device validation.

The unsigned IPA requires user signing/sideloading. Keep the Live Activity extension when signing. Live Activity/Dynamic Island timers and existing QR/session behavior remain available; there is no iOS Home Screen widget. Background execution remains subject to iOS. The current summary takes precedence over older version notes below.

## What is implemented

- Xbox library with search, remembered A–Z/Z–A/last-played sorting, completion colouring and base-completion indicator after a verified achievement read.
- Modern and legacy achievement reads, search/filter/sorting, expandable verified DLC/base/update sections matched by Title ID, platform, achievement ID and name.
- Manual unlock/retry, multi-selection into an account-specific persisted queue, per-item delays, pause/resume, remove items and remove achievements confirmed unlocked by Xbox.
- Foreground title-presence heartbeats and elapsed timer alongside the queue, failure/completion notification options and stop-on-presence-failure.
- Microsoft browser sign-in with PKCE and automatic in-app callback capture, optional manual authorization, separate event-token entry, iOS Keychain storage. No passwords, tokens or real accounts in tests, logs or artifacts.
- Bundled current fork catalogue, prepared from committed source. Only deterministic Replace operations with checked targets, placeholders and title/service identity are enabled. Unsupported edition routes (including Minecraft's hosted route and the remaining guarded edition aliases) remain disabled until their full guards are ported. Disabled author titles stay disabled. See catalogue-report.json in the build artifact for exact coverage.

## First connection

In Settings choose **Sign in with Microsoft**. The embedded Microsoft webpage returns automatically after sign-in; PKCE, state and single-use callback checks remain enabled. The page uses an app-local persistent WebKit store when remembering is enabled; no scripts inspect passwords, page contents or cookies. If the embedded page does not support your sign-in method, use **Safari / manual sign-in** and paste the final redirect into the app. Never share that URL.

Choose **Xbox desktop** to use the same OAuth profile as the desktop fork, or **Xbox iOS** to retain the previous beta profile. Existing sessions retain their profile until you sign in again. A successful library read does not guarantee presence permissions: a heartbeat 403 is reported as a service-specific refusal and is not retried or bypassed.

Alternatively paste a current **Xbox service authorization** (`XBL3.0 x=…;…`) and tap Connect. This is separate from the **event token** (`x:XBL3.0 x=…;…`), saved in its own section after connecting. Automatic event-token acquisition is not included. A standalone app still needs valid service credentials; it cannot read another app's protected account store.

The app does not unlock anything on launch, sign-in, refresh or queue load. Manual Unlock/Retry and Start/Resume send actions. Collector acceptance is shown separately from confirmed Xbox progress. Queues skip achievements Xbox already reports unlocked; manual Retry can resend them. Refresh to confirm progress, including delayed credit.

## Foreground operation and current limitations

By default, queues and presence pause when the app enters the background; reopening never automatically resumes them. The opt-in iOS 26 continued-processing options below can keep an explicitly started finite session active while iOS grants runtime. While running in the foreground the screen is kept awake. Do not rely on background execution or a heartbeat stopping instantly after a lost connection. Queue requests interrupted during submission are marked for review rather than automatically retried. Timers show this app session, not Xbox-recorded playtime.

This beta has no Steam/Epic/Ubisoft clients, Xbox 360/GFWL write/profile tools, Windows token capture, stats editor or Windows executable updater. DLC metadata is bundled with each mobile build; new supported event titles can sync through the public event feed. Legacy achievements are read-only. Live authentication, event credit and presence still require physical-device testing; simulator/core tests do not prove service compatibility.

## Build and sign

The **iOS standalone beta** GitHub workflow prepares resources, runs synthetic tests plus every enabled generated recipe, builds arm64 iPhone/iPad and simulator apps, verifies simulator launch plus synthetic Keychain save/read/delete and creates `AchievementLabs-iOS-0.12.2-UNSIGNED.ipa`. Simulator-only ad-hoc entitlements are used to exercise Keychain APIs; they are never included in the unsigned device app. Device signing must supply the normal application identity and Keychain entitlements.

**The unsigned IPA cannot be installed directly.** It needs signing/provisioning appropriate for your device or distribution method. No Apple account, certificate or provisioning profile is bundled. Never commit private signing credentials. No App Store/TestFlight release is claimed.

For a Mac/Xcode development build:

```sh
python3 mobile/tools/prepare_catalogue.py
brew install xcodegen
cd mobile
xcodegen generate
open AchievementLabsMobile.xcodeproj
```

In Xcode select your signing team, use a unique bundle identifier you control, select your device and build/run. Xcode supplies the signing/provisioning flow. For simulator-only testing a signing team is unnecessary. The CI artifact has an unsigned device IPA for a separate user-controlled signing flow.

Documentation: [Apple device distribution](https://developer.apple.com/documentation/xcode/distributing-your-app-to-registered-devices).

Desktop source, licences and author attribution remain in the repository. No private Xbox signing material or TLS trust bypass is included. Mobile sources are independent of the Windows projects and build workflows.

Browser sign-in: use the embedded Microsoft page for automatic callback capture, or Safari / manual sign-in as a fallback. PKCE, state validation and a one-use response protect both paths. Account authorization is verified with Xbox before replacing the saved account. Event tokens remain separate. With Remember sign-in enabled, OAuth renewal credentials and Xbox authorization are stored in the iOS Keychain. Rotated renewal credentials are saved before the next Xbox exchange. The renewed XUID must match the active account. Disabling remembering removes saved credentials; Disconnect also clears Microsoft browser data held by this app. No Microsoft password is stored.

## Mobile 0.3.0 refinements

Compact inline navigation and library rows; nested Xbox image fallback; profile gamerpic and gamerscore from Xbox; loaded library game/achievement counts; desktop star-and-laurel Mythic artwork and persistent, account-specific completion evidence. “Mythic · known” counts verified base completions plus fully completed titles, excluding multi-game hubs. It is a local display calculation, not a new Xbox award. Settings can scan unchecked titles sequentially; leaving the app stops that scan after the current read. Bundled shared totals fill missing totals only for a matching title/platform and never replace account unlocked counts.

Auto Unlock has reference gamertag + title ID timing import, fresh same-title achievement matching, preview-only queue building, editable minute delays, estimated finish and collapsible options. Timing gaps reproduce intervals, not historical achievement timestamps. Library/game shortcuts and typed-ID lookup show title art in both Auto Unlock and Title Spoofing. Xbox-recorded playtime is read separately when available. No claim is made that a heartbeat will increase it. The desktop OAuth profile is available for compatibility testing; actual spoofing permission remains subject to Xbox accepting the request.

## Session persistence and spoofing diagnostics (0.3.0)

Remember sign-in is enabled by default. Sign in once after upgrading to capture renewal credentials (older versions discarded them). The embedded Microsoft page uses a persistent app-local WebKit data store when remembering is enabled and an ephemeral store otherwise. The app never reads page passwords, cookies or unrelated password-manager entries. Native token/code fields are tagged as code fields rather than passwords. Safari remains available when in-app AutoFill suggestions are incorrect; it needs the manual redirect return.

Known-expiring authorization renews before reads or actions; read-only requests may retry once after a 401 using renewed authorization. Failed unlock requests and failed heartbeat requests are not automatically replayed. Renewal is shared across concurrent callers, rotates its protected credentials before Xbox exchange, validates the connected XUID and preserves the separate event token. Account changes invalidate an in-flight renewal. Turning remembering off removes the protected saved session and Microsoft web data; Disconnect does the same. If Microsoft revokes renewal, browser sign-in is required.

Spoofing 403 is now reported without repeatedly recommending the already-tested desktop profile. A failed heartbeat produces sanitized server-code/correlation diagnostics and one read-only profile check to distinguish general authorization failure from heartbeat-specific refusal. No authorization, event token, OAuth credentials or raw response bodies are included in Copy diagnostics. A successful profile check does not grant spoofing permission.

## Public sync and PC transfer (0.3.0)

Settings includes automatic event-data sync, Sync now, mobile release checking, update notifications and an automatic library refresh toggle. Public checks use an unauthenticated, cookie-free client and never send Xbox credentials. The hourly GitHub `Mobile public event feed` workflow verifies the original repo’s latest published Events.zip digest and archive structure, preserves JSON operation order, rejects unsupported replacement types and publishes only new titles absent from the bundled source. Native import revalidates every recipe and title/service identity before atomically caching it. Bundled corrections and disabled edition routes cannot be overwritten. New titles are Testing; accepted event requests still need progress verification. The public feed currently matches upstream v1.0.8, so zero new titles is a valid sync result.

App update checks inspect published IPA release assets in the fork and ignore Windows-only releases. If no iOS release is published, the app says so; build artifacts alone do not appear as releases. Checks and notifications run while the app is active, not as an always-on background service. Installation remains a Signulous signing/install operation.

The matching Windows build adds Settings → iPhone / iPad sync → Export encrypted mobile sign-in. This writes a `.labslink` file encrypted with AES-256-GCM and a separate random 256-bit pairing key. Import is accepted for 10 minutes. Transfer the file to the phone, import it in mobile Settings → PC sync, and enter the key. Xbox verifies the authorization and XUID before a preview offers Connect this PC account or Import event token only. Event-only import requires the same account and preserves native OAuth renewal. Connecting the PC account imports current authorization/event credentials; OAuth renewal credentials stay on PC, so sync again when the imported authorization expires. No credential listener, LAN port or token-upload service is created. Keep both file and key private.

The existing desktop app icon is prepared as an opaque 1024-pixel iOS asset during the build. Library refresh runs after sign-in and when returning to the app, with a short throttle; screenshot/self-test modes block automatic network maintenance.

## QR sync and launch page (0.4.0)

Home offers Microsoft sign-in and Sync from PC. Settings → Launch selects Home, Library, Auto Unlock, Title Spoofing or Settings for subsequent launches; disconnected installs start at Home.

In the matching Windows build, click the mobile icon beside Attach Xbox PC app. On mobile choose Home → Sync from PC → Scan PC QR codes. Keep the camera pointed at the looping codes until all are collected, then verify and connect the shown account or import only its event token. The codes contain private session information; do not share screenshots. QR collection happens entirely between the screen and camera, without a LAN listener or third-party relay. Transfers expire after ten minutes and do not include Microsoft renewal credentials.

The encrypted file/key fallback remains available. Its picker accepts the custom extension as a document; content, size, encryption, expiry and account identity are still validated.

## Event-only QR and homepage (0.5.0)

PC mobile sync offers separate Sign-in + event token QR and Event token QR only modes. Event-only packets exclude account authorization and Microsoft renewal credentials; iOS requires an existing sign-in for the matching XUID and only offers event-token import. Account and event transfers retain their ten-minute import expiry.

Home now displays a larger gamerpic, gamerscore, games, unlocked achievements, known Mythic completions and fully completed games, with recent games, quick navigation and pull-to-refresh. Counts use the same account-specific library and completion data as Library.

## Categorized settings and QR speed (0.6.0)

Settings now has Account, Sync, Library and App tabs. A toolbar QR scanner is available from every tab; Sync also includes direct scanning and file import. Account settings explain the full PC sign-in QR route for heartbeat refusals; event-only sync preserves account authorization. A PC-transferred session working where browser sign-in fails is evidence of a session-specific difference, not proof of the exact server-side permission rule.

The PC QR window uses mutually exclusive radio selectors and a large active-mode heading. Cycle speed defaults to 0.5 seconds, with 0.8-second Balanced and the previous 1.2-second Slow options. Manual navigation and repeated loops remain available when a phone misses frames.

## 0.7.0 experimental iOS background queue
On iOS 26+, Settings → App offers opt-in background continuation for a finite Auto Unlock queue. Start explicitly while foregrounded and wait for the status to confirm iOS granted time. Actual processed actions advance Apple's system progress UI; accepted submissions are still not proof of unlocks. Long delays can lead to expiration. A running spoofer can optionally accompany the authorized queue, then stops if the queue ends in background. Standalone indefinite spoofing is not supported. Older iOS versions retain foreground-only operation.

Cancellation, permission refusal, an unanswered request or task expiration never automatically resumes the queue. Signulous/device runtime behavior still needs physical-device testing. When opted in, account Keychain items use AfterFirstUnlockThisDeviceOnly and queue files use CompleteUntilFirstUserAuthentication to allow protected background saves after lock; disabling the option restores foreground storage protection. Other caches retain their existing protection.


## 0.8.0 TLS diagnostics and shared background session
Event sending defaults to Microsoft's documented `mobile.events.data.microsoft.com` collector, retaining the same event payloads and authentication. The Windows collector is a manual comparison option. TLS trust is not relaxed, and failed actions are not replayed automatically. Settings → App → Test secure connections performs unauthenticated HEAD requests only, displaying safe endpoint/error codes without tokens. A successful TLS check is not proof an event is authorized or an achievement unlocked.

A finite standalone background spoofing option (5–120 minutes, off by default) can share the existing continued-processing request with Auto Unlock on iOS 26. Status text shows processed queue actions and spoof elapsed time; no extra ActivityKit card is created. CPU/network background permission remains up to iOS, including during long waits. Previously failed system cards may require manual dismissal.

Queue delay fields now have a Done keyboard toolbar control that validates, saves, and dismisses the keypad. Start is disabled while a delay is being edited.

Unauthenticated macOS URLSession probes for 0.8.0 reproduced URL error -1202 on the Windows collector while the mobile collector, achievement API and heartbeat host completed TLS. This does not verify authenticated event acceptance or physical-device background runtime.

## 0.8.1 Windows collector trust and stop diagnostics
The mobile collector established TLS but rejected this app's Windows-format event requests with HTTP 400. Xbox event sending now uses the original Windows collector. An unauthenticated Apple TLS probe identified its missing trusted anchor as Microsoft Root Certificate Authority 2011. Its DER fingerprint was independently checked against Microsoft's HTTPS PKI repository (SHA1 8f43288ad272f3103b6fb1428485ea3014c0bcfe, SHA256 847df6a78497943f27fc72eb93f9a637320a02b561d0a91b09e87a7807ed7c61).
Only the exact Windows collector host can use this embedded Microsoft anchor. Security.framework still validates TLS hostname, certificate expiry, signatures and chain; no accept-all trust delegate or ATS exception is added. Event redirects are refused. Tests include the real public chain, an expired chain, a modified signature and a different host. No user tokens or unlock requests are used by these tests.
Spoofing stop diagnostics now retain whether iOS ended permission, background mode was off, the session reached its limit, or a heartbeat failed. A failed DELETE cleanup request cannot overwrite the actual stop reason. iOS can still end runtime; this does not guarantee unattended spoofing. The system background status retains the 15-second update interval. Updating it every second would create additional app work; ActivityKit's native timer rendering would need a separate custom widget, which is not included in this maintenance build.
The supplied XAU release ZIP contained only a README. Compiled IPA metadata/strings indicate an ActivityKit extension, extended audio mode and server-side event jobs; this is evidence of components, not a full source-code comparison. No XAU server, private service, entitlement check or proprietary implementation is reused.


## 0.8.2 iOS ATS integration and explicit extended audio
The macOS command-line TLS probe did not exercise the iOS app's ATS policy. This build adds Apple's domain-scoped manual-trust exception only for v20.events.data.microsoft.com (no subdomains, TLS 1.2 minimum, forward secrecy retained). The application still uses HTTPS exclusively, validates the exact Microsoft root, hostname, chain/signatures and expiry, and rejects event redirects and plaintext redirects. No global arbitrary-load allowance is present. A credential-free HEAD test now runs inside the actual iOS simulator app and must pass; the manifest scope is checked there too.
Extended background audio is an explicit opt-in under Settings → App, off by default. With standalone background spoofing enabled, starting spoofing can loop local silent PCM with mixWithOthers during the configured 5–120 minute session. It uses the public audio playback APIs; this is a sideloading compatibility mode, not a promise of indefinite runtime or App Store eligibility. It can use more battery and stops on audio interruptions, session limit, errors or explicit Stop. It never starts on launch or automatically resumes after interruption. Start spoofing before the queue to share extended runtime. Protected storage uses the existing background opt-in protection policy.
A new WidgetKit/ActivityKit extension displays extended-mode spoof elapsed time using native timer text, plus queue handled counts. It never labels accepted event requests as verified unlocks. One custom activity is used; it is not shown while the system background task is active. It ends when the session stops and shows stale status if app updates cease. The app updates state at 15-second intervals; timer rendering does not require one-second app wakeups. Physical-device audio/runtime behavior and Signulous signing of the extension still require testing.



Modern Warfare Remastered (Xbox One) uses a reviewed 50-achievement mapping. The mobile catalogue checks legacy PC-keyed and corrected Xbox-keyed sets against fixed event signatures. Unknown IDs or changed semantics disable the whole title. Downloaded/imported feeds cannot replace bundled edition mappings.
