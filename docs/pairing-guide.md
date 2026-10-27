# Pairing for spoofing

These steps are also available in Settings → Guides on desktop, iOS and Android.

## PC to Mac

1. Connect the same Xbox account in both apps, on the same local network. On Windows, attach Achievement Labs to the signed-in Xbox PC app.
2. Windows: Settings → Sync → Pair a Mac on this network.
3. Mac: Settings → Sync → Pair with Windows on this network. Select the PC and choose Pair selected PC.
4. Compare the code and approve on both devices. Select Remember this device on both for future recognition.
5. Mac: open Game Session, select a game and start spoofing.

If discovery misses the PC, enter the private address and port displayed on Windows. After one remembered pairing, future searches also query that PC directly. Both pairing windows must remain open during the transfer. Remembered trust identifies a device; each transfer still needs approval.

## PC to iOS / Android

1. On Windows, attach Achievement Labs to your signed-in Xbox PC app.
2. Open Settings → Sync → Show mobile QR code. Choose Sign-in only, or Sign-in + event token if you also need the event token.
3. Stop active sessions on mobile and tap Scan QR. Keep the camera on the cycling codes until all frames are collected.
4. Review the gamertag, then choose Connect this PC account (iOS) / Connect PC account (Android).
5. Open Game Session, select the game and start spoofing.

QR transfer does not need the same Wi-Fi network. Event-token-only import does not replace account authorization or grant spoofing access. Sign-in-only import retains an existing event token only when it belongs to the same account. The phone's account changes only after the user reviews and confirms the PC import.

## Expiry and reconnecting

The ten-minute mobile QR/file deadline and two-minute network invitation are transfer deadlines, separate from the Xbox authorization lifetime. Mac pairing no longer has an artificial twenty-minute deadline. Known Xbox expiry is respected; opaque authorization expiry is displayed as unknown. Server refusal stops spoofing without an automatic heartbeat retry. Pair again to obtain fresh authorization when needed.

Mac authorization stays in memory until removed, disconnected or the app closes. Device trust remains protected with DPAPI / Keychain-backed storage. Remembering devices does not automatically renew authorization. Existing mobile secure-storage and background-session limits remain in force. Importing never starts spoofing or an unlock queue. Keep sign-in QR codes, transfer files and pairing keys private.
