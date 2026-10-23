# QR sync

Use PC 1.0.8.9, iOS 0.12.0 or Android 0.6.0 for these new transfer modes.

## Sign-in only

Click the mobile icon beside Attach Xbox PC app. Choose Sign-in only, then scan using the mobile PC sync scanner and confirm the verified account. These codes contain the current Xbox authorization, without an event token or Microsoft renewal credentials. An existing mobile event token is retained only when it belongs to that same Xbox account. Sign-in + event token and Event token only remain available. Sign-in codes expire after ten minutes; keep them private.

## Saved queue QR sync

Pause Auto Unlock before transferring. Each device must be signed into the same Xbox account.

- PC to phone/tablet: Auto Unlock → Saved game queues → Export queue QR. On mobile choose the queue scanner, rather than the sign-in scanner, and keep the cycling codes in view until all are collected. Confirm Import saved queues.
- Between phones/tablets: show the queue QR on one device and scan with the queue scanner on the other.
- Phone/tablet to PC: choose Share all QR images, transfer every PNG to PC, then select them together using Import queue QR images. Review the import before confirming. PC currently imports images rather than scanning a live webcam.
- PC to PC: Save all QR images, then import those images on the destination PC.

Mobile queue QR controls are in Auto Unlock → Saved game queues. Android also exposes them in Settings → Sync.

Queue codes contain saved game/achievement identifiers, names, progress and delays, with no sign-in or event tokens. They are portable data, not encrypted credentials; avoid sharing them publicly. Codes have a checksum, account validation and bounded sizes. They are not proof that an achievement unlocked. Imports verify against Xbox, preserve uncertain submissions as review items, and never start a session automatically. Matching saved game queues may be replaced after successful validation. An active mobile game queue is protected from being overwritten.

QR transfers support up to 128 KiB. Use the existing JSON sync file for larger exports or large numbers of frames. Sync is a manual snapshot transfer, not continuous cloud synchronization.
