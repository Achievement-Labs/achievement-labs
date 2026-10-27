# Achievement Labs Linux preview

Linux is a separate executable built from the desktop codebase. Windows keeps its existing target, EXE packaging, Xbox PC attachment and Windows integrations. Mac produces a separate `.app`; Linux produces a self-contained x64 executable folder in a `.tar.gz`. Only one platform flag may be selected for a build.

The initial Linux target is Ubuntu 24.04 x64 with X11 or XWayland. Extract the complete archive and run `./AchievementLabs`. No separate .NET runtime is needed. The package currently uses system GTK/WebKit and Secret Service libraries:

```sh
sudo apt-get install libx11-6 libice6 libsm6 libfontconfig1 libsecret-1-0 python3-gi gir1.2-webkit2-4.1 libnotify-bin
```

Use a desktop session with an unlocked Secret Service provider such as GNOME Keyring. A missing or locked keyring never falls back to plaintext credential storage. Saved credentials and remembered pairing trust are encrypted with AES-GCM; their random encryption key is held by Secret Service. Microsoft sign-in uses an ephemeral WebKitGTK window and captures the registered callback over an anonymous pipe, with state/PKCE validation by the existing OAuth client. It does not require changing the registered redirect URI. Closing the sign-in window cancels the operation.

Library, achievements, Game Session, event-token retrieval and Xbox 360 profile tools reuse desktop services. Windows process attachment, GFWL injection and the existing local Windows launcher integrations are hidden/unavailable. Steam and other native Linux integrations are separate work.

## Title presence

Sign in to the same account on Linux and Windows. Attach the Windows app to the Xbox PC app, open its Settings → Sync → Pair a Mac window (also accepts this Linux client), and choose Pair with Windows in Linux Settings → Sync. Compare the code and approve on both devices. Then select a title and start Game Session spoofing explicitly.

Linux sends heartbeats directly using the transferred Windows authorization. The PC is needed again when fresh authorization is required. Remembered trust does not renew authorization. Imported presence authorization remains in memory and is separate from Linux sign-in and event tokens. Known expiry and refusal stop spoofing. Mac-only/ Linux-only presence permission is not established by an ordinary sign-in or event token. Actual Linux Xbox sign-in, token issuance and live presence require user device acceptance testing; CI uses fake credentials and loopback only.

## Build and updates

Run `bash tools/linux/build-preview.sh` on Linux with .NET 9 SDK, Python 3 and Node.js installed. The desktop Xbox 360 preparation uses the existing checksum-pinned public build without executing it. Generated shared identity files remain ignored and are embedded only through the approved signing-resource path.

Linux updates are manual downloads from `Achievement-Labs/achievement-labs`; Linux never selects or installs the Windows updater. Event data continues to use the desktop repository. No live event catalog is changed by this port.

The Linux workflow builds a standalone package and checks real Secret Service encryption, native GTK callback capture, synthetic pairing and native Avalonia startup/rendering under Xvfb. Real GNOME/KDE, Wayland and hardware acceptance remain separate from headless CI.
