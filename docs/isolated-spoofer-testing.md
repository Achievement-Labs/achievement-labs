# Isolated spoofer testing

Developers can exercise the presence/spoofing path without the GUI, in two layers.

## Offline request-shape checks (no network, no Xbox app)

```sh
dotnet run --project tests/AchievementLabs.Enhancements.Tests -c Release -- --spoof
```

Pins the exact wire format in `AchievementLabs.Core.SpoofRequest`:

- `SanitizeXauth` trims a raw memory-scanned token to `XBL3.0 x=<digits>;<payload>`.
- `BuildHeartbeatBody` produces `{"titles":[{"expiration":600,"id":<numeric>,"state":"active","sandbox":"RETAIL"}]}` — presence refuses a quoted string title id.
- `HeartbeatUris(xuid)` is the four-URI presence chain (userpresence and presence-heartbeat, xuid and /me variants). Send walks the chain until a 2xx and aborts immediately on 401; stop/cleanup DELETEs across all four (404 on a variant never set is normal).

## Live probe (real presence requests)

```sh
dotnet run --project tests/AchievementLabs.SpoofCli -c Release -- scan
dotnet run --project tests/AchievementLabs.SpoofCli -c Release -- probe [--title ID]
dotnet run --project tests/AchievementLabs.SpoofCli -c Release -- run --title ID [--minutes N]
dotnet run --project tests/AchievementLabs.SpoofCli -c Release -- stop [--xuid XUID]
```

- `scan` lists Xbox app processes the reader scans (XboxPcApp, XboxPcAppCE, XboxPcAppFT — the token migrates to the full-trust host across app updates).
- `probe` reads the signed-in token from app memory, validates it against the profile API, prints the gamertag/xuid, and (with `--title`) sends one heartbeat, printing the status and response body.
- `run` sends heartbeats every 5 minutes for `--minutes` (default 10) and then runs stop cleanup. A `run` is a REAL spoof: presence stays set until `stop` clears it.
- Tokens are never printed. Heartbeat request/response logs land in `Documents/AchievementLabs/Debug/Heartbeat` for the app's own Debug logging path.

Verified live (test title 1285166951, probe → HTTP 200, stop → HTTP 200).
