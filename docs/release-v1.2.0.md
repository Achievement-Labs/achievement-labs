# Achievement Labs v1.2.0

Windows feature release. Mac and Linux packages stay on their separate track.

## In-game overlay (Windows)

- Press **Alt+U** in a game to open an overlay, and **Alt+U** or **Esc** to close it. The game stays in the foreground and isn't minimized.
- The overlay detects the running Steam or Xbox game automatically. It has four tiles: current game achievements with unlocks, library search (copy a title or app ID), Achievement Mirror, and Xbox/Steam presence.
- It uses the main app's colour and OLED settings.
- Exclusive-fullscreen games can't be drawn over, so you get a Windows notification instead. Switch the game to borderless or windowed to use the overlay.

## Xbox 360 profiles

- A public, verified Xbox 360 GPD catalog. **Add to profile** inserts a locked, stripped GPD using the existing backup and awaiting-console-sync flow.
- An opt-in **Contribute GPD** setting, off by default. It never uploads anything automatically.
- The desktop flow now matches Android/iOS: open an exported profile, export a signed profile or the original backup, date/time pickers, new sorts, and separate Xbox reference and Exophase date modes.
- Signing uses only the bundled identity, and the signing setup UI has been removed.

## Event catalog

- 561 titles: 352 Supported, 89 Partially Supported, and 120 Testing.
- New: Bastion, Plague Inc: Evolved, and The Binding of Isaac: Rebirth (Testing).
- Recipe and template updates for existing titles, synced from the author's source catalog. Existing support statuses are kept.

## Not verified

Unlocks, Mirror and presence from the overlay haven't been tested against a live game. Adding a catalog GPD hasn't been tested on a real console.
