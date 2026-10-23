# Achievement Labs Android 0.2.1 beta

Standalone Android 8+ APK. The interface is packaged inside the APK; Xbox traffic, QR decoding, encryption, authentication and queue execution run on the device. No PC server or hosted website is needed.

Home includes gamerpic, gamerscore, game and achievement counts, known Mythic completions and recent games. Library supports search and remembered sorting. Achievement pages include shared DLC groups, selection, retry and queues. Auto Unlock supports reference-gamertag intervals, editable saved delays, ETA, remove completed, failure controls and notifications. Title Spoofing runs alongside the queue while the app remains active. Settings has Account, Sync, Library and App categories.

The existing Windows mobile QR dialog works with Android. Scan full sign-in codes or event-token-only codes using the in-app camera. A verified account confirmation is required. Event-only imports require the same signed-in XUID and preserve mobile renewal. Microsoft browser sign-in is also available; PC-transferred authorization may have different heartbeat permissions. Tokens stay in app-private Android Keystore-encrypted storage. Passwords are handled by Microsoft's sign-in page, not stored by this app.

Shared catalogue generation preserves desktop edition fixes and blocked routes. Public event sync adds validated new titles without replacing existing IDs. No live Xbox unlocks are performed by the build. Accepted requests are never shown as confirmed unlocks. Legacy Xbox 360/GFWL unlocks are read-only. The first Android build supports QR transfer; encrypted file import and the Windows-only tools are not included.

Queues and spoofing pause on backgrounding. This version does not run a background foreground-service worker. App update checks look for Android APK assets on published fork releases; Windows/iOS-only releases are ignored.

## Build

JDK 17, Gradle 8.11.1, Android SDK 35 and Android Gradle plugin 8.9.2. Generate the mobile catalogue with `python3 mobile/tools/prepare_catalogue.py`, copy `mobile/Catalogue` to `android/app/src/main/assets/catalogue` and the existing desktop PNG icon to `android/app/src/main/res/drawable/app_icon.png`, then `gradle -p android testDebugUnitTest assembleDebug lintDebug`. GitHub Actions also verifies launch on an API 35 emulator with synthetic data and all account/network actions blocked in preview mode.

This is a debug-signed test APK, not a Google Play release. The CI debug signing key is temporary; a stable user-owned signing key is needed before promising seamless upgrades or publishing. Back up account-independent settings if replacing builds signed with a different key; Android requires uninstalling the previous app in that case.

0.1.1 fixes Android null continuation tokens on the last achievement page, normalizes artwork URL shapes and allows the Xbox store image host. Library sorting is cached, search is debounced without rebuilding its input, and costly navigation blur is removed.

## Android 0.2.0
Achievements can be sorted within their existing DLC groups (original, A–Z, Z–A, highest/lowest gamerscore), and filtered All / Locked / Unlocked. Preferences are saved.

Active queues and title spoofing now use an application-owned runtime and a data-sync foreground service. Allow notifications before starting. The notification has Pause queue and Stop all actions; background operation can be disabled in Settings → App. No automatic restart after force-stop, reboot or process death. All sessions pause after six hours; Android 15 can enforce an earlier shared data-sync allowance. Battery restrictions and network loss may delay requests.

Settings → App can download and verify a published Android release APK, then open Android's installer. Package ID, newer version code, GitHub asset SHA-256 digest, and identical signing certificates are required. Install permission and final installation remain under Android/user control. There must first be a public Android release asset with a GitHub SHA-256 digest.

### Permanent release signing
Configure these GitHub Actions repository secrets once: `LABS_ANDROID_KEYSTORE_BASE64`, `LABS_ANDROID_KEY_ALIAS`, `LABS_ANDROID_STORE_PASSWORD`, `LABS_ANDROID_KEY_PASSWORD`. The workflow signs the delivered APK with that key and removes its temporary keystore. Keep a secure backup of the permanent keystore; never commit it. The build now fails when any signing secret is missing; it never falls back to a temporary test key. A different key requires a one-time reinstall; the updater refuses to erase existing data.
