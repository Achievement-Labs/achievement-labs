# Achievement Labs Android — unofficial fork beta 0.6.8

> Experimental mobile event-token test: see [test instructions](../docs/mobile-event-token-test.md). Public fork .15 remains unchanged. Successful token issuance and acceptance by the event collector are separate checks.

Standalone Android app. App updates and downloads use [thevampirebat/achievement-labs](https://github.com/thevampirebat/achievement-labs/releases); event JSON comes from the [upstream Achievement Labs project](https://github.com/Achievement-Labs/achievement-labs). Feature contributions are submitted upstream.

Game Session combines Title Spoofing and Auto Unlock with explicit Start/Stop controls, reference-gamertag queue timing, saved queues, editable delays, timers and notifications. Library/achievement shortcuts open the selected game in Game Session. Search includes platform filters and Roman/Arabic numeral matching.

Xbox 360 imports an exported retail CON profile, displays its game library and achievement descriptions, filters/sorts rows, and edits selected locked achievements with offline, current, custom or reference dates. Unlock selected automatically backs up, rehashes and re-signs the private working copy. Export signed profile produces a separate file; console/USB copying remains a user step. Imports are limited to 32 MB and supported STFS layouts. GFWL and server-based 360 achievement requests remain read-only. No exported profile is uploaded. See [mobile Xbox 360 details](../docs/mobile-xbox360.md).

Microsoft sign-in and PC QR sync stay native. Sign-in tokens use Android Keystore-encrypted app-private storage. Queue JSON/QR transfer contains progress and delays, not sign-in credentials. Background queue/spoof sessions use an ongoing notification when enabled; Android may stop or delay them. Sessions never resume automatically after an app restart. Accepted requests still require an Xbox progress refresh.

## Build and checks

Use JDK 17, Gradle 8.11.1 and Android SDK 35. Run `python3 mobile/tools/prepare_catalogue.py` and `python3 mobile/tools/prepare_360.py`, copy `mobile/Catalogue` to `android/app/src/main/assets/catalogue` and the desktop icon to `android/app/src/main/res/drawable/app_icon.png`, then run `gradle -p android testDebugUnitTest assembleDebug lintDebug`. The 360 preparation statically reads the pinned, checksum-verified public fork Windows build; it never executes it and never reads a personal profile. Generated signing inputs stay ignored by Git.

CI runs Java/JavaScript checks and emulator screens, including synthetic profile import/unlock/signed-save through the actual WebView and native file bridge. Public APK packaging requires the existing permanent Android signing key. Console acceptance and Xbox synchronization of exported profiles still need device validation.
