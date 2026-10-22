# Session timers and Android home-screen widget

The iOS spoof timer is bounded by the expiry of the last accepted Xbox heartbeat (600 seconds), rather than a 45-second UI refresh. Each accepted heartbeat renews the deadline. This is elapsed session time, not Xbox-recorded playtime. If the app stops updating, the card eventually asks you to check status. Force-quitting cannot guarantee an immediate stop request to Xbox.

Live Activity updates are serialized and coalesced. The next achievement, game and absolute countdown change when the queue advances; timers animate locally rather than depending on a status push every second.

## Android

Delay fields are in minutes, including fractional minutes. Existing queues and exported files keep seconds internally, so an existing 120-second delay displays as 2 minutes.

Auto Unlock displays Running/Paused, session elapsed time and a next-unlock countdown. The ongoing notification also uses a native countdown. Checking/submitting is shown when no unlock delay is running.

To add the home-screen widget, open Settings → App → Add Labs sessions widget, or long-press an empty area of the home screen and choose Widgets → Achievement Labs. It shows spoof elapsed time, next game/achievement and an unlock countdown. Pause queue and Stop spoof act on the current session. Open/resume opens the queue for review, without automatically sending an achievement. Widgets do not restart a killed service. Cached widget status can lag; the widget shows its last update time.

## iPhone and iPad

The iOS app uses a Live Activity for its lock-screen and Dynamic Island session displays. Keep the LabsSessionWidget extension when signing: it provides the Live Activity, despite the target name. It does not require App Groups.

The iPhone/iPad Home Screen widget and its shared-storage option have been removed because the current signing setup cannot provision a custom App Group. No App Groups entitlement or signing template is included. Android retains its Home Screen widget.

## Manual queue sync

1. Pause Auto Unlock on the source device.
2. Expand Saved game queues and choose Export queue sync file. Android also has these controls in Settings → Sync.
3. Copy the JSON file to the other device using Files, a cable or your preferred file-sharing method.
4. Sign into the same Xbox account there, pause Auto Unlock, and choose Import queue sync file.
5. Load a saved game, review progress and delays, then press Start when ready.

This is manual file transfer, not automatic cloud synchronization. Imports verify achievement IDs and names against Xbox metadata and do not start unlocking. A game already in the active mobile queue is protected from being overwritten. PC rejects unresolved previously submitted items until progress is refreshed or their source status is explicitly reset; it does not silently replay them.
