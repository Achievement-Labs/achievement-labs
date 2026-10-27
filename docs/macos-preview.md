# Achievement Labs — Apple Silicon Mac desktop preview

This is an unpublished prototype of the Windows/Avalonia desktop interface, targeting Apple Silicon (M1 or newer) and macOS 14 or later. It is the unofficial vampirebat fork of https://github.com/Achievement-Labs/achievement-labs.

The first build retains Home, Library and search, achievements, Game Session/spoofing/queues, appearance controls, and the Xbox 360 exported-profile editor with backups and signed export. Original profiles remain untouched. Copying a profile back to a console stays a separate step.

Windows Xbox-app attachment, Windows account-broker token retrieval, GFWL injection and local Windows Steam/Epic/Ubisoft integrations are unavailable in this preview. Windows-only navigation is hidden. The app uses a native Microsoft sign-in window instead; the registered callback is captured automatically and the app connects when sign-in finishes. Close that window to cancel. Remembered sign-in and event tokens use authenticated encryption backed by this Mac user's Keychain. A rejected/locked Keychain does not fall back to plaintext. Sign-in and live Xbox requests still require testing on your own Mac.

Use Get event token on this Mac in Settings after Microsoft sign-in. The mobile-style proof-of-possession device/RPS user/XSTS exchange checks the Xbox profile and event identity before saving; Microsoft renewal rotation is preserved even if Xbox refuses the exchange. Issuer expiry is encrypted separately and bound to the exact token fingerprint. Existing tokens remain on failure, and manual token entry is also available. Live issuance and collector acceptance still need testing on the user’s Mac.

For this first preview, Mac updates are manual downloads from https://github.com/thevampirebat/achievement-labs/releases. It does not select or install Windows update packages. Event JSON/catalogue updates continue to use Achievement-Labs/achievement-labs.

The .app is self-contained and needs no separate .NET installation. It is ad-hoc signed for testing, not Developer ID signed or notarized. Normal public Mac distribution still needs that signing/notarization step. Do not disable macOS security protections.

Build on a Mac with .NET 9 SDK: `bash tools/macos/build-preview.sh`. Output: `artifacts/macos-preview/AchievementLabs-macOS-0.1.0.4-arm64.zip`. The app contains only the approved reusable CON identity; no personal profiles, account sessions or custom signing stores are packaged. CI uses a synthetic profile to check editing/signing and renders disconnected screens without Xbox account/event requests.

## Preview 0.1.0.4 startup checks

The app now keeps its self-contained runtime beside the entry point instead of relying on runtime symlinks. The downloadable ZIP is extracted and launched through macOS LaunchServices from /, with normal startup and a visible native window checked separately from the synthetic profile smoke test.

If it still does not open, check ~/Library/Logs/AchievementLabs/startup.log. This report includes only version, runtime/architecture, startup stages and exception type names; it excludes sign-in data, profile contents and exception messages. A managed startup failure also displays a native alert. A failure before the .NET runtime starts may produce no report. The app remains ad-hoc signed for testing, without Developer ID notarization.

When Apple Silicon runners are unavailable, the ARM64 package can be cross-built and ad-hoc signed on an Intel Mac. CI then launches a native Intel sibling built from the same source to exercise extracted ZIP/LaunchServices startup, Keychain and profile operations. BUILD-INFO.json identifies the native check architecture; an Intel sibling pass does not claim native ARM64 execution. The ARM64 package separately checks that libhostfxr is an actual ARM64 Mach-O file rather than an extracted symlink's text.


Preview 0.1.0.4 uses a native, ephemeral WebKit sign-in window. The Mac button now captures the registered Microsoft desktop callback before the landing page clears its authorization code and completes the existing PKCE/state-protected exchange automatically. Callback data stays in an anonymous process pipe; it is not written to logs, browser history, the clipboard or a callback file. Closing the window cancels sign-in. Windows retains its existing manual fallback. A synthetic native WebKit test verifies callback capture without requesting Microsoft or using account credentials; actual sign-in and two-factor behaviour still require the user's Mac.


Preview 0.1.0.4 adds actual macOS Notification Center support inside the main app process. Settings → Notifications → Test Mac notification requests alert/sound permission if undecided and submits a local test. Ordinary alerts never prompt for permission automatically. Permission denial and submission errors are shown separately; macOS Focus/banner preferences can still suppress a banner. CI only reads authorization status, never requests permission or delivers notifications.

The Mac spoofer now uses the connected, renewed OAuth authorization rather than attempting Windows Xbox-app process access. It verifies the Xbox account before its first heartbeat, stops on refusal, and sends no cleanup heartbeat after 401/403. Windows retains the established Xbox-app authorization source. A successful profile read or event token does not establish heartbeat permission; Windows Xbox-app authorization remains the verified working route. No successful native Mac spoofing is claimed.

## Preview 0.1.0.5: PC-to-Mac presence comparison

A controlled test of PC-origin account authorization on Mac. On Windows attach the Xbox PC app, then Settings > Sync > Mobile sync (iOS / Android) > Export encrypted mobile sign-in. Transfer the .labslink file and separate key privately to your Mac within ten minutes. Sign in to the same account on Mac, stop queues/spoofing, then Settings > Sync > PC-to-Mac spoofer test > Import PC authorization. Enter the key and confirm. Import performs only a profile read; start Game Session spoofing separately. A successful HTTP heartbeat is explicitly labelled PC-paired authorization. Report only the HTTP status and whether that label appears.

The importer accepts current account/sign-in transfers, rejects event-only, expired, wrong-account, tampered and ambiguous envelopes, and respects any known account-authorization expiry. Opaque token expiry remains unknown; a separate twenty-minute local test limit bounds the pairing. Event tokens in a full sync file are ignored. The pairing stays in memory, clears on disconnect, never replaces Mac OAuth/library/event credentials, cannot renew itself, and stops rather than silently reverting to Mac authorization when expiring. Remove PC pairing restores the usual Mac spoofer path. This experiment applies only to the Game Session spoofer; Auto Unlock retains its existing authorization. No working Mac heartbeat or independent Mac grant is claimed until a live user test. No real account/token or heartbeat is used by CI.

## Preview 0.1.0.6: remembered Microsoft sign-in and easier pairing

Remember sign-in is visible below the main Mac Sign in button. Enable it before signing in, or while connected to save the current Microsoft sign-in using existing Keychain-backed protection. The choice persists, startup reconnects a remembered sign-in, and the main sign-in button tries it before opening Microsoft again. Disabling the choice removes the default saved Microsoft session while leaving the current in-memory connection active. Existing protected saved sessions migrate with remembering enabled. Refresh rotation is saved before the subsequent Xbox authorization exchange.

PC-to-Mac pairing remains in Settings > Sync. The import dialog now offers Paste key (reads the clipboard only on that explicit click); Cmd-V also works. Each newly encrypted .labslink file needs its corresponding key. Remembering Microsoft sign-in does not save or renew PC pairing. The successful user test proves PC-origin authorization works from Mac with our existing heartbeat request; a Mac-only issuing flow is still unresolved. No new login/client-ID experiments are silently enabled. Hosted CI uses synthetic credentials to check preference reload, protected session round trip and opt-out deletion without contacting Xbox or Microsoft.

The PC pairing tile also offers Compare authorization metadata. It compares only public header format/allowlisted algorithm names and whether key references match; raw references, tokens and account/title/device claims are excluded. No network request is made. The result can guide the issuing-path investigation, but matching references do not establish matching permissions. Unknown/legacy/ambiguous headers remain unknown. The report is selectable for sharing without credentials.
