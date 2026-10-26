# Xbox 360 local unlock/save test

Originally developed as a local prototype. On 2026-10-24 the user authorized
a PR to Achievement-Labs/achievement-labs and prerelease publication to
thevampirebat/achievement-labs alongside the cross-platform search updates.

## Quick start

Run the Windows 1.0.8.14 `AchievementLabs.exe`. Load your exported profile,
open a game, tick the achievements and click **Unlock selected**. The app backs
up, edits, rehashes, resigns and verifies the working profile in one operation.
The original raw export is not the save target.

The default is **Offline (no date)**. Choose **Online (now)** to stamp the current
UTC time, or **Custom dates** for a starting date and spacing. Online here is the
local profile flag; Xbox Live synchronization still happens on the console.

### Standalone signing

The packaged Windows build includes the validated CON signing identity read from the
user-supplied Horizon executable. It starts ready to sign without Horizon installed,
a Horizon import dialog or a previously saved signing setup. Achievement Labs owns
the rehash/sign/write implementation; it does not execute Horizon code.

Static inspection of Horizon's `Horizon.Server.Config.loadConKeys` found seven
embedded arrays (128, 64, 64, 64, 64, 64 and 424 bytes) loaded with
`RuntimeHelpers.InitializeArray` and `addSetting`, rather than a network key fetch
or derivation from an exported profile. The key/certificate are checked for
consistency before use. No key bytes are printed in the inspection or test logs.

The private build input lives under ignored `artifacts/private-signing/` and is
included only when the local build explicitly supplies `LabsXbox360SigningFile`.
It is not checked into source control. Unlike the earlier preview ZIPs, this executable
**does contain a signing identity embedded inside the app's Core assembly** so
it can operate independently. It does not contain personal profiles, Xbox tokens,
a console keyvault, the Horizon executable or a separate raw signing-data file.
The private build input remains excluded from source control and PRs.

A custom imported Horizon/keyvault identity can still be remembered encrypted for
the Windows account. Valid custom setup takes priority; otherwise the bundled
identity loads automatically. Forgetting custom setup restores the built-in default.
Source builds without a supplied identity retain the one-time manual setup option.
Console acceptance and sync remain unverified; successful local signature/hash
checks alone do not prove that the console/server will accept the result.

### Controls

- Valid extracted working profiles enable editing automatically after integrity checks;
  no manual ownership checkbox is required.
- Achievement descriptions appear in the middle of wide rows and beneath the name
  on narrow windows. The unlock controls use a compact panel.
- The Xbox 360 library opens directly without separate Recently played cards.
- Unlocked achievement rows have a green tint and green text/status.
- Filter All / Locked only / Unlocked only; sort ID, A–Z, Z–A or date. Checked
  selections survive filtering, including hidden rows. Empty selection does not
  unlock everything. Already unlocked achievements are skipped.
- **Reference dates** defaults to **Xbox Live**, matching the normal unlocker's
  gamertag-based Xbox API approach. Attach the Xbox account, enter a reference
  gamertag and click **Load dates**. The 360 endpoint resolves the exact decimal
  Xbox Title ID and matches achievement IDs plus gamerscore. It skips duplicated
  IDs, incorrect metadata, unknown unlock state and invalid/future online dates.
  Reference lookup only reads data; **Unlock selected** writes the working copy.
  Xbox account-generation changes invalidate in-flight results. No Exophase
  request is sent in this mode. Unit/working-copy checks use fixture Xbox data;
  actual account-level reference access still needs testing.
- Optional **Exophase profile** and **Xbox gamertag (Exophase)** modes accept
  an Exophase profile name or Xbox gamertag.
  Choose the input type, enter the name and click **Load dates**. The lookup keeps
  Xbox 360 games only and automatically matches an exact normalized game name
  (including numeral aliases). If there are multiple editions or no exact name,
  choose the correct game from the list and load again. Dates are matched to the
  selected achievements without changing the profile; **Unlock selected** applies
  them. Missing/ambiguous names are skipped. Changing the reference name, game or
  selection clears the old dates. File import remains a collapsed fallback.
- **Details & tools** contains paths, backup information, reload, signing setup
  management and the separate **Copy to USB** action. USB copying requires a
  single matching detected profile and verifies its backup before replacement.
- Ordinary EXE startup uses the normal home page, automatic Xbox app attachment and
  background update checks. Only explicitly passing `--xbox360-local-test` enables
  the isolated Xbox 360 preview mode.

The normal workflow has no separate preview, staging or save buttons. Technical
checks and backups remain active beneath the single unlock action. Repeated
clicks during an operation do not trigger a second save.

## What the save verifies

The write boundary requires a supported retail CON profile layout (0x971A header,
171–28,900 allocated blocks), nonzero profile ID, extracted `.con` path, valid
header signature/metadata and active STFS data/hash-table integrity. Added games
awaiting console sync remain blocked. The loaded snapshot must still match.

Only achievement state/timestamp fields and sync records are edited; immutable
achievement metadata is validated. Dashboard achievement/gamerscore totals are
updated, and last-played is never moved backwards by older reference timestamps.
The replacement GPDs preserve byte lengths. A verified pre-save backup is retained;
the new package is rehashed, RSA/SHA-1 signed and read back before an atomic local
replacement. Verification compares every achievement against its expected record,
checks all dashboard titles/totals and preserves the profile ID. Contradictory
achievement/gamerscore totals are rejected instead of being guessed or repaired. The prior file is retained for recovery if final verification fails.

The app validates the working file rather than asking for an ownership attestation.
This is not a verified Xbox account binding: a profile ID is not an XUID. A locally written online flag does
not prove an online service unlock. Xbox 360 must still accept and synchronize
the exported profile; those console-side outcomes remain unverified.

## Reference imports

Manual dates require ISO 8601 with a UTC offset and must fall between Xbox 360
launch and now. Offline unlocks are undated. Exophase file import accepts earned
JSON (`{"list":[{"name":"Example","timestamp":1609459200}]}`) or saved fully
loaded HTML; timestamp 0 means offline. Exact normalized name matching skips
missing, ambiguous and already unlocked achievements. Empty selection never means
all achievements. Inputs are bounded to 2 MB / 5,000 entries. Profile-name and gamertag fetching are implemented with bounded requests to fixed
Exophase HTTPS hosts, pagination, Xbox 360 filtering, cancellation/timeouts and no
Xbox tokens, cookies or profile uploads. External redirects and oversized responses
are rejected. The current live probe returned **HTTP 403**, so actual live loading
could not be validated here. The app reports that error and offers file import;
it does not bypass the website's restrictions. Fixture tests cover lookup and dates.
The public profile/game-list shapes were checked against the
[upstream client source](https://github.com/MatisAgr/Nintendo-Exophase-Importer/blob/main/Exophase/ExophaseClient.cs)
and [models](https://github.com/MatisAgr/Nintendo-Exophase-Importer/blob/main/Exophase/ExophaseModels.cs);
these endpoints may change independently of this app.

## Achievement descriptions

The ordinary achievements page and Game session Auto Unlock queue now show wrapping
descriptions in a dedicated column at wider sizes, or below the name at smaller
sizes. Queue descriptions are persisted with new queues and retained when a row
status changes. Older saved queues attempt a read-only metadata refresh when loaded;
lookup failure leaves the queue usable with its existing delays and progress.

## Validation

A separate working copy of the supplied exported profile was used to test single-action and selected
online and offline unlocks, signing, full active STFS hash-tree verification,
achievement readback, dashboard totals, unchanged unselected achievements and
verified backups. Original bytes were compared before/after and remained unchanged.
Negative checks reject damaged blocks, altered metadata, invalid signing imports
and profile switching with pending edits. Additional checks cover encrypted signing restore without Horizon, corrupted saved setup, forgetting custom setup, startup with bundled signing and no cache/Horizon import, one-click online/offline/custom/imported saves, empty selections, future dates and double clicks. Fixture reference lookups exercise name entry, platform filtering, matching, stale-date clearing and blocked-request handling without profile writes. No USB write or console sync was attempted.
Private test files remain outside the repository/distributable. Deliberately damaged
negative-test copies must not be used; their untouched backups are retained.

The Windows desktop/test build, reference parser/filter/sort tests and actual
headless application initialization/scroll checks are also run before packaging.

## Limits of the safeguards

These checks protect profile-file integrity and consistency. Full safeguard parity
with Horizon has not been established, and there is no server-flag/enforcement
prevention guarantee. Valid rehashing/resigning does not prove Microsoft will
accept edited achievements. Neither console acceptance nor server synchronization
has been tested. Xbox's current [Community Standards](https://www.xbox.com/en-US/legal/community-standards)
prohibit tampering; Microsoft has also [documented achievement/Gamerscore tampering enforcement](https://news.xbox.com/en-us/2008/03/25/gamerscore-correction/).

Windows 1.0.8.14 is one self-contained executable, including the .NET runtime, app libraries
and helper executables. It extracts bundled dependencies automatically into the
Windows .NET bundle cache when launched; no companion folder or launcher script is
required. Its larger EXE size includes files previously shipped beside the launcher.
No Horizon executable is shipped.
