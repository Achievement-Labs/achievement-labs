# Mobile sign-in spoofing and global scanner preview

Unpublished test builds: Android 0.6.9 (17), iOS 0.12.11 (29).

Start spoofing now prepares a separate Xbox user/device presence authorization using the Microsoft sign-in and refresh token on this phone. It verifies the same account and expiry before use, caches the result in existing protected account storage, and renews before expiry. PC QR/manual sessions retain their existing authorization path. Event-token issuance is separate and is not required by this exchange. Library authorization and saved event tokens are retained if preparation fails. Rotated refresh tokens are saved before later Xbox requests, and queue/heartbeat renewal is serialized.

This is an implementation to test, not proof that Xbox accepts mobile heartbeats. Automated tests use synthetic HTTP responses. A live phone test must confirm the server accepts Start spoofing after native Microsoft sign-in with Remember sign-in enabled and no PC transfer. A failed 403 stops the session; it is not retried as a transient network failure. No account credentials or live heartbeat requests are used by CI.

Scan QR is in every main/detail page header on Android and the shared iOS page toolbar, including Xbox 360 and Session Settings. Existing automatic detection of sign-in, event-token and saved-queue QRs is retained. Imports require review and active sessions to be paused. Android's 360 scanner leaves the editor/working profile in place.

Phone test: sign in with Microsoft on the phone (iOS: use the Xbox iOS profile to test mobile-device credentials), open a game in Game Session, tap Start spoofing, and check for a heartbeat acceptance message. Stop explicitly, then test again after reopening the app. If Xbox refuses the token, share the status/HTTP code only, never tokens or redirect URLs.

App update feeds remain thevampirebat/achievement-labs. Event catalogue remains Achievement-Labs/achievement-labs. Mac work is parked and excluded from this branch.

## Follow-up: device proof and issuer expiry

The 0.12.11 phone test still returned heartbeat 403 while the same-account profile read succeeded. That establishes a presence-specific refusal; it does not establish expired sign-in or identify the exact missing permission.

Test candidates Android 0.6.10 (18), iOS 0.12.12 (30) retain the per-device P-256 proof key alongside its presence token in existing encrypted account storage and attach a signature to POST and DELETE presence requests. The signature covers policy version, Windows timestamp, method, path, authorization and exact body bytes. Imported PC authorization keeps its original path. Existing mobile credentials without their proof key are renewed before use. Keys are generated locally and never printed, sent to public feeds, QR exports or UI diagnostics. This corrects an unsigned-request omission; live Xbox acceptance remains unverified and Xbox may still enforce additional title/client permissions.

Event-token issuer NotAfter is saved and displayed with time remaining. Metadata is bound to the exact token fingerprint and cannot follow a replacement/manual/QR token. Unknown expiry stays unknown, including previously acquired tokens whose metadata was discarded. Refresh/replacement never invents a new lifetime. The issuer expiry is a scheduled bound, not a guarantee against earlier rejection or revocation. Get a fresh mobile event token once to populate it.

References: Microsoft proof-key request signing https://learn.microsoft.com/en-us/gaming/gdk/docs/services/fundamentals/s2s-auth-calls/s2s-calls/live-title-service-calls-xbox-live and XSTS token structure https://learn.microsoft.com/en-us/gaming/gdk/docs/services/fundamentals/s2s-auth-calls/service-authentication/security-tokens/live-security-tokens .

## Phone-only app/title experiment

Unpublished candidates: Android 0.6.11 (19), iOS 0.12.13 (31).

Both earlier iOS attempts received heartbeat 403, including the request carrying a device signature. The PC path instead reads the native Xbox PC app authorization before sending each heartbeat; its exact grants are not known. A successful library read does not establish permission to send presence.

This candidate refreshes the phone browser login, obtains the selected app's SiSU user/title authorization and exchanges UserTokens + DeviceToken + TitleToken + ProofKey in a signed XSTS request. The same generated proof key signs device, SiSU, XSTS and heartbeat requests. iOS respects the selected Xbox iOS/iOS or Xbox desktop/Win32 profile; Android uses its existing desktop OAuth/Win32 pair. It never silently changes OAuth client or borrows a PC credential. The earlier user/device-only presence cache is invalidated by a flow marker. Missing title authorization or a refusal ends that attempt without fallback/retries. Event-token requests retain their previous exchange.

Install over the previous test app. Keep your native phone Microsoft login with Remember sign-in enabled; no PC QR transfer is needed. Open Game Session and tap Start spoofing. On iOS, Diagnostics will identify App/title + device proof (experimental). If it is refused, share only the displayed stage, HTTP code and sanitized diagnostics. Do not send authentication tokens, private keys or sign-in redirect URLs. An Xbox desktop profile can also be signed in through the phone browser; that name describes the OAuth profile and does not require a PC. Changing profile is a separate test, not an automatic fallback.

Automated transport tests use synthetic grants and verify app/device matching, title inclusion, proof continuity, refresh rotation, same-account validation, expiry, missing-title/refusal behavior and preservation of library/event credentials. They do not prove live heartbeat permission. Phone acceptance remains unverified until tested.

Protocol reference: https://github.com/PrismarineJS/prismarine-auth/blob/master/src/TokenManagers/XboxTokenManager.js (SiSU title-token retention and signed XSTS title/device exchange). No example credential logging is included in our implementation.
