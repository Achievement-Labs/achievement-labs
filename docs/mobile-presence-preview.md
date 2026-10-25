# Mobile sign-in spoofing and global scanner preview

Unpublished test builds: Android 0.6.9 (17), iOS 0.12.11 (29).

Start spoofing now prepares a separate Xbox user/device presence authorization using the Microsoft sign-in and refresh token on this phone. It verifies the same account and expiry before use, caches the result in existing protected account storage, and renews before expiry. PC QR/manual sessions retain their existing authorization path. Event-token issuance is separate and is not required by this exchange. Library authorization and saved event tokens are retained if preparation fails. Rotated refresh tokens are saved before later Xbox requests, and queue/heartbeat renewal is serialized.

This is an implementation to test, not proof that Xbox accepts mobile heartbeats. Automated tests use synthetic HTTP responses. A live phone test must confirm the server accepts Start spoofing after native Microsoft sign-in with Remember sign-in enabled and no PC transfer. A failed 403 stops the session; it is not retried as a transient network failure. No account credentials or live heartbeat requests are used by CI.

Scan QR is in every main/detail page header on Android and the shared iOS page toolbar, including Xbox 360 and Session Settings. Existing automatic detection of sign-in, event-token and saved-queue QRs is retained. Imports require review and active sessions to be paused. Android's 360 scanner leaves the editor/working profile in place.

Phone test: sign in with Microsoft on the phone (iOS: use the Xbox iOS profile to test mobile-device credentials), open a game in Game Session, tap Start spoofing, and check for a heartbeat acceptance message. Stop explicitly, then test again after reopening the app. If Xbox refuses the token, share the status/HTTP code only, never tokens or redirect URLs.

App update feeds remain thevampirebat/achievement-labs. Event catalogue remains Achievement-Labs/achievement-labs. Mac work is parked and excluded from this branch.
