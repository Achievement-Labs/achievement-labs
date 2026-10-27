# Achievement Labs Mac desktop preview

Mac is a separate Apple Silicon application built from this desktop repository, targeting macOS 14 or later. The Windows EXE retains its own build and integrations. The Mac port originates in contributor PR #24; Linux extends the same desktop services with a separate package.

The app retains Home, Xbox library/search, achievements, Game Session, stat tools, settings, and Xbox 360 profile editing/export. Windows Xbox-app process attachment, GFWL injection, and the existing local Windows launcher integrations are unavailable and hidden.

## Sign-in and credentials

Microsoft sign-in opens a native ephemeral WebKit window. It captures the registered callback before the landing page consumes it; the existing OAuth client validates origin, state and PKCE. Closing the window cancels sign-in. Remembered sign-in, event tokens and pairing trust use authenticated encryption backed by the current user's Keychain. Locked or unavailable secure storage never falls back to plaintext.

Get event token in Settings uses the Mac Microsoft sign-in and verifies matching Xbox identity before saving. A library sign-in or event token does not establish title-presence permission.

For the contributor-reported working presence route, connect the same account on Windows and Mac, attach Windows to its Xbox PC app, and approve pairing from both Settings → Sync screens. The Mac sends heartbeats directly using the transferred Windows authorization. A private encrypted .labslink file and its separate key can be imported as a fallback. Import never starts spoofing. Known issuer expiry is honored; unknown expiry is not replaced with an artificial twenty-minute limit. Authorization remains in memory and clears on disconnect/app close. Remembering devices does not implement unattended renewal. Re-pair when authorization expires or is refused. See [pairing guide](pairing-guide.md).

## Packaging and validation

On Mac, run `bash tools/macos/build-preview.sh` with .NET 9 SDK, Python 3 and Node installed. Desktop-only Xbox 360 resource preparation lives under `tools/xbox360`, independent of the mobile repository. The self-contained `.app` needs no separate .NET installation. It is ad-hoc signed for preview testing and is not Developer ID signed/notarized.

Output: `artifacts/macos-preview/AchievementLabs-macOS-0.1.0.9-arm64.zip`. App updates are manual downloads from `Achievement-Labs/achievement-labs`; Windows update packages are excluded. Event data uses the same official desktop repository. No personal profile or account data is packaged.

CI checks synthetic pairing, WebKit callback capture, Keychain protection, normal LaunchServices startup, page rendering and signed synthetic profile export without live Xbox operations. On its configured Intel runner it cross-builds the ARM64 package and runs a native Intel sibling; BUILD-INFO.json records that distinction. Package byte validation does not establish native ARM64 execution. Live account sign-in, event acceptance and presence still require separate user-device acceptance testing.

Startup diagnostics are under `~/Library/Logs/AchievementLabs/startup.log` and contain stages and exception types rather than credentials. Do not disable macOS security protections to install this preview.
